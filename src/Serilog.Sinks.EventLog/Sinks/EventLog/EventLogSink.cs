﻿// Copyright 2014 Serilog Contributors
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace Serilog.Sinks.EventLog
{
	/// <summary>
	/// Writes log events as documents to the Windows event log.
	/// </summary>
	/// <remarks>Be aware of changing the source/logname, see: http://stackoverflow.com/questions/804284/how-do-i-write-to-a-custom-windows-event-log?rq=1</remarks>
	public class EventLogSink : ILogEventSink
	{
		readonly ITextFormatter _textFormatter;
		readonly string _source;
	    readonly string _logName;
	    const string APPLICATION_LOG = "Application";

	    /// <summary>
	    /// Construct a sink posting to the Windows event log, creating the specified <paramref name="source"/> if it does not exist.
	    /// </summary>
	    /// <param name="source">The source name by which the application is registered on the local computer. </param>
	    /// <param name="logName">The name of the log the source's entries are written to. Possible values include Application, System, or a custom event log.</param>
	    /// <param name="textFormatter">Supplies culture-specific formatting information, or null.</param>
	    /// <param name="machineName">The name of the machine hosting the event log written to.</param>
	    /// <param name="manageEventSource">If false does not check/create event source.  Defaults to true i.e. allow sink to manage event source creation</param>
	    public EventLogSink(string source, string logName, ITextFormatter textFormatter, string machineName,
	        bool manageEventSource)
	    {
	        if (source == null) throw new ArgumentNullException(nameof(source));
	        if (textFormatter == null) throw new ArgumentNullException(nameof(textFormatter));

	        //The source is limitted in length and allowed chars
	        //see: https://msdn.microsoft.com/en-us/library/e29k5ebc%28v=vs.110%29.aspx
	        source = source.Substring(0, Math.Min(source.Length, 212));
	        source = source.Replace("<", "_");
	        source = source.Replace(">", "_");

	        _textFormatter = textFormatter;
	        _source = source;
	        _logName = logName;

	        if (string.IsNullOrWhiteSpace(_logName))
	            _logName = APPLICATION_LOG;

	        var sourceData = new EventSourceCreationData(source, logName) {MachineName = machineName};

	        if (manageEventSource)
	        {
	            var exists = true;
	            System.Diagnostics.EventLog existingLogWithSource = null;

	            if (string.IsNullOrWhiteSpace(logName) && !System.Diagnostics.EventLog.SourceExists(source, machineName))
	            {
	                exists = false;
	            }
	            else
	            {
	                exists = SourceExistsInLog(source, logName, machineName);

	                if (!exists)
	                {
	                    //stash a reference to this guy so we can add a log entry noting the logs your looking for are in the
	                    //log previously associated with "source"
	                    existingLogWithSource = GetLogFromSource(source, machineName);

	                    if (existingLogWithSource != null)
	                    {
	                        System.Diagnostics.EventLog.DeleteEventSource(source, machineName);
	                    }
	                }
	            }

	            if (exists) return;

	            System.Diagnostics.EventLog.CreateEventSource(sourceData);

	            if (existingLogWithSource != null)
	            {
	                var log = System.Diagnostics.EventLog.GetEventLogs().FirstOrDefault(l => l.Log == _logName);
	                if (log == null)
	                    return;

	                var metaSource = $"serilog-{_logName}";
	                if (!System.Diagnostics.EventLog.SourceExists(metaSource, machineName))
	                    System.Diagnostics.EventLog.CreateEventSource(new EventSourceCreationData(metaSource, _logName)
	                    {
	                        MachineName = machineName
	                    });

	                log.Source = metaSource;
	                log.WriteEntry(
	                    message: $"Event source {source} was previously registered in log {existingLogWithSource.Log}. " +
	                             $"The source has been registered with the new log, {logName}, however a computer restart is required " +
	                             $"before event logs will appear in {logName} with source {source} instead of {existingLogWithSource.Log}.",
	                    type: EventLogEntryType.Warning,
	                    eventID: (int) LogEventLevel.Warning);
	            }
	        }
	    }

	    /// <summary>
        /// Returns name of EventLog that contains the registered source
        /// </summary>
        /// <param name="source"></param>
        /// <param name="machineName"></param>
        /// <returns></returns>
	    private System.Diagnostics.EventLog GetLogFromSource(string source, string machineName)
	    {
	        return System.Diagnostics.EventLog.GetEventLogs().ToList().FirstOrDefault(l => SourceExistsInLog(source, l.Log, machineName));
	    }

	    private bool SourceExistsInLog(string sourceName, string logName, string machineName)
	    {
            return FindSourceNamesFromLog(logName, machineName)?.Any(s => s == sourceName) ?? false;
	    }

        /// <summary>
        /// Returns list of sources associated with specific log
        /// </summary>
        /// <remarks>
        /// EventLog Api doesn't support this natively so we have to use the Registry
        /// </remarks>
        private static List<string> FindSourceNamesFromLog(string logName, string machineName)
        {
            if (machineName.Trim() == ".") machineName = Environment.MachineName;

            var hive = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, machineName);
            var keyLog = hive.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Eventlog\{logName}");
            return keyLog?.GetSubKeyNames().ToList();
        }

	    /// <summary>
	    /// Emit the provided log event to the sink.
	    /// </summary>
	    /// <param name="logEvent">The log event to write.</param>
	    /// <remarks>
	    /// <see cref="LogEventLevel.Debug" />, <see cref="LogEventLevel.Information" /> and <see cref="LogEventLevel.Verbose" /> are registered as <see cref="EventLogEntryType.Information" />.
	    /// <see cref="LogEventLevel.Error" />, <see cref="LogEventLevel.Fatal" /> are registered as <see cref="EventLogEntryType.Error" />.
	    /// <see cref="LogEventLevel.Warning" /> are registered as <see cref="EventLogEntryType.Warning" />.
	    /// The Event ID in the Windows log will be set to the integer value of the <paramref name="logEvent"/>'s <see cref="LogEvent.Level"/> property, so that the log can be filtered with more granularity.</remarks>
	    public void Emit(LogEvent logEvent)
	    {
	        if (logEvent == null) throw new ArgumentNullException("logEvent");

	        EventLogEntryType type;
	        switch (logEvent.Level)
	        {
	            case LogEventLevel.Debug:
	            case LogEventLevel.Information:
	            case LogEventLevel.Verbose:
	                type = EventLogEntryType.Information;
	                break;

	            case LogEventLevel.Error:
	            case LogEventLevel.Fatal:
	                type = EventLogEntryType.Error;
	                break;

	            case LogEventLevel.Warning:
	                type = EventLogEntryType.Warning;
	                break;

	            default:
	                SelfLog.WriteLine("Unexpected logging level, writing to EventLog as Information");
	                type = EventLogEntryType.Information;
	                break;
	        }

	        var payloadWriter = new StringWriter();
	        _textFormatter.Format(logEvent, payloadWriter);

	        //The payload is limitted in length and allowed chars
	        //see: https://msdn.microsoft.com/en-us/library/e29k5ebc%28v=vs.110%29.aspx
	        var payload = payloadWriter.ToString();
	        if (payload.Length > 31839)
	        {
	            //trim
	            payload = payload.Substring(0, 31839);
	        }

	        var log = System.Diagnostics.EventLog.GetEventLogs().FirstOrDefault(l => l.Log == _logName);
	        log.Source = _source;
	        log.WriteEntry(
	            message: payload,
	            type: type,
	            eventID: (int) logEvent.Level);
	    }
	}
}
