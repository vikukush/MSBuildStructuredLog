﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Logging.StructuredLogger
{
    /// <summary>
    /// A logger that serializes all incoming BuildEventArgs in a compressed binary file (*.binlog). The file
    /// can later be played back and piped into other loggers (file, console, etc) to reconstruct the log contents
    /// as if a real build was happening. Additionally, this format can be read by tools for
    /// analysis or visualization. Since the file format preserves structure, tools don't have to parse
    /// text logs that erase a lot of useful information.
    /// </summary>
    /// <remarks>The logger is public so that it can be instantiated from MSBuild.exe via command-line switch.</remarks>
    public sealed class BinaryLogger : ILogger
    {
        // version 2: 
        //   - new BuildEventContext.EvaluationId
        //   - new record kinds: ProjectEvaluationStarted, ProjectEvaluationFinished
        // version 3:
        //   - new ProjectImportedEventArgs.ImportIgnored
        // version 4:
        //   - new TargetSkippedEventArgs
        //   - new TargetStartedEventArgs.BuildReason
        // version 5:
        //   - new EvaluationFinished.ProfilerResult
        // version 6:
        //   - Ids and parent ids for the evaluation locations
        // version 7:
        //   - Include ProjectStartedEventArgs.GlobalProperties
        // version 8:
        //   - This was used in a now-reverted change but is the same as 9.
        // version 9:
        //   - new record kinds: EnvironmentVariableRead, PropertyReassignment, UninitializedPropertyRead
        // version 10:
        //   - new record kinds:
        //      * String - deduplicate strings by hashing and write a string record before it's used
        //      * NameValueList - deduplicate arrays of name-value pairs such as properties, items and metadata
        //                        in a separate record and refer to those records from regular records
        //                        where a list used to be written in-place
        // version 11:
        //   - new record kind: TaskParameterEventArgs
        // version 12:
        //   - add GlobalProperties, Properties and Items on ProjectEvaluationFinished
        // version 13:
        //   - don't log Message where it can be recovered
        //   - log arguments for LazyFormattedBuildEventArgs
        //   - TargetSkippedEventArgs: added OriginallySucceeded, Condition, EvaluatedCondition
        // version 14:
        //   - TargetSkippedEventArgs: added SkipReason, OriginalBuildEventContext
        // version 15:
        //   - new record kind: FileUsedEventArgs
        // version 16:
        //   - AssemblyLoadBuildEventArgs
        // version 17:
        //   - Added extended data for types implementing IExtendedBuildEventArgs
        // version 18:
        //   - Making ProjectStartedEventArgs, ProjectEvaluationFinishedEventArgs, AssemblyLoadBuildEventArgs equal
        //     between de/serialization roundtrips.
        //   - Adding serialized events lengths - to support forward compatible reading
        // version 19:
        //   - GeneratedFileUsedEventArgs exposed for brief period of time (so let's continue with 20)
        // version 20:
        //   - TaskStartedEventArgs: Added TaskAssemblyLocation property
        // version 21:
        //   - TaskParameterEventArgs: Added ParameterName and PropertyName properties
        // version 22:
        //    - extend EnvironmentVariableRead with location where environment variable was used.
        // version 23:
        //    - new record kinds: BuildCheckMessageEvent, BuildCheckWarningEvent, BuildCheckErrorEvent,
        //    BuildCheckTracingEvent, BuildCheckAcquisitionEvent, BuildSubmissionStartedEvent
        // version 24:
        //    - new record kind: BuildCanceledEvent
        // version 25:
        //    -  add extra information to PropertyInitialValueSetEventArgs and PropertyReassignmentEventArgs and change parsing logic of Message property in them.

        // This should be never changed.
        // The minimum version of the binary log reader that can read log of above version.
        internal const int ForwardCompatibilityMinimalVersion = 18;

        // The current version of the binary log representation.
        // Changes with each update of the binary log format.
        internal const int FileFormatVersion = 25;
        // The minimum version of the binary log reader that can read log of above version.
        // This should be changed only when the binary log format is changed in a way that would prevent it from being
        // read by older readers. (changing of the individual BuildEventArgs or adding new is fine - as reader can
        // skip them if they are not known to it. Example of change requiring the increment would be the introduction of strings deduplication)
        internal const int MinimumReaderVersion = 18;

        public static bool IsNewerVersionAvailable { get; set; }

        private Stream stream;
        private BinaryWriter binaryWriter;
        private BuildEventArgsWriter eventArgsWriter;
        private ProjectImportsCollector projectImportsCollector;
        private string _initialTargetOutputLogging;
        private string _initialLogImports;

        /// <summary>
        /// Describes whether to collect the project files (including imported project files) used during the build.
        /// If the project files are collected they can be embedded in the log file or as a separate zip archive.
        /// </summary>
        public enum ProjectImportsCollectionMode
        {
            /// <summary>
            /// Don't collect any files during the build.
            /// </summary>
            None,

            /// <summary>
            /// Embed all project files directly in the log file.
            /// </summary>
            Embed,

            /// <summary>
            /// Create an external .ProjectImports.zip archive for the project files.
            /// </summary>
            ZipFile
        }

        /// <summary>
        /// Gets or sets whether to capture and embed project and target source files used during the build.
        /// </summary>
        public ProjectImportsCollectionMode CollectProjectImports { get; set; } = ProjectImportsCollectionMode.Embed;

        private string FilePath { get; set; }

        /// <summary> Gets or sets the verbosity level.</summary>
        /// <remarks>
        /// The binary logger Verbosity is always maximum (Diagnostic). It tries to capture as much
        /// information as possible.
        /// </remarks>
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

        /// <summary>
        /// Gets or sets the parameters. The only supported parameter is the output log file path (for example, "msbuild.binlog"). 
        /// </summary>
        public string Parameters { get; set; }

        /// <summary>
        /// Initializes the logger by subscribing to events of the specified event source.
        /// </summary>
        public void Initialize(IEventSource eventSource)
        {
            _initialTargetOutputLogging = Environment.GetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING");
            _initialLogImports = Environment.GetEnvironmentVariable("MSBUILDLOGIMPORTS");

            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", "true");
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", "1");
            bool logPropertiesAndItemsAfterEvaluation = true;

            ProcessParameters(out bool omitInitialInfo);
            var replayEventsSource = eventSource as IBinaryLogReplaySource;

            try
            {
                string logDirectory = null;
                try
                {
                    logDirectory = Path.GetDirectoryName(FilePath);
                }
                catch (Exception)
                {
                    // Directory creation is best-effort; if finding its path fails don't create the directory
                    // and possibly let the FileStream constructor below report the failure
                }

                if (logDirectory != null)
                {
                    Directory.CreateDirectory(logDirectory);
                }

                stream = new FileStream(FilePath, FileMode.Create);

                if (CollectProjectImports != ProjectImportsCollectionMode.None && replayEventsSource == null)
                {
                    projectImportsCollector = new ProjectImportsCollector(FilePath, CollectProjectImports == ProjectImportsCollectionMode.ZipFile);
                }

                if (eventSource is IEventSource3 eventSource3)
                {
                    eventSource3.IncludeEvaluationMetaprojects();
                }

                if (logPropertiesAndItemsAfterEvaluation && eventSource is IEventSource4 eventSource4)
                {
                    eventSource4.IncludeEvaluationPropertiesAndItems();
                }
            }
            catch (Exception e)
            {
                string errorCode;
                string helpKeyword;
                string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out errorCode, out helpKeyword, "InvalidFileLoggerFile", FilePath, e.Message);
                throw new LoggerException(message, e, errorCode, helpKeyword);
            }

            stream = new GZipStream(stream, CompressionLevel.Optimal);

            // wrapping the GZipStream in a buffered stream significantly improves performance
            // and the max throughput is reached with a 32K buffer. See details here:
            // https://github.com/dotnet/runtime/issues/39233#issuecomment-745598847
            stream = new BufferedStream(stream, bufferSize: 32768);
            binaryWriter = new BinaryWriter(stream);
            eventArgsWriter = new BuildEventArgsWriter(binaryWriter);

            if (projectImportsCollector != null)
            {
                eventArgsWriter.EmbedFile += EventArgsWriter_EmbedFile;
            }

            if (replayEventsSource != null)
            {
                if (CollectProjectImports == ProjectImportsCollectionMode.Embed)
                {
                    replayEventsSource.EmbeddedContentRead += args =>
                        eventArgsWriter.WriteBlob(args.ContentKind, args.ContentStream);
                }
                else if (CollectProjectImports == ProjectImportsCollectionMode.ZipFile)
                {
                    replayEventsSource.EmbeddedContentRead += args =>
                        ProjectImportsCollector.FlushBlobToFile(FilePath, args.ContentStream);
                }

                // If raw events are provided - let's try to use the advantage.
                // But other subscribers can later on subscribe to structured events -
                //  for this reason we do only subscribe delayed.
                replayEventsSource.DeferredInitialize(
                    // For raw events we cannot write the initial info - as we cannot write
                    //  at the same time as raw events are being written - this would break the deduplicated strings store.
                    // But we need to write the version info - but since we read/write raw - let's not change the version info.
                    () =>
                    {
                        binaryWriter.Write(replayEventsSource.FileFormatVersion);
                        binaryWriter.Write(replayEventsSource.MinimumReaderVersion);
                        replayEventsSource.RawLogRecordReceived += RawEvents_LogDataSliceReceived;
                        // Replay separated strings here as well (and do not deduplicate! It would skew string indexes)
                        replayEventsSource.StringReadDone += strArg => eventArgsWriter.WriteStringRecord(strArg.StringToBeUsed);
                    },
                    SubscribeToStructuredEvents);
            }
            else
            {
                SubscribeToStructuredEvents();
            }

            void SubscribeToStructuredEvents()
            {
                // Write the version info - the latest version is written only for structured events replaying
                //  as raw events do not change structure - hence the version is the same as the one they were written with.
                binaryWriter.Write(FileFormatVersion);
                binaryWriter.Write(MinimumReaderVersion);

                if (!omitInitialInfo)
                {
                    LogInitialInfo();
                }

                eventSource.AnyEventRaised += EventSource_AnyEventRaised;
            }
        }

        private void EventArgsWriter_EmbedFile(string filePath)
        {
            if (projectImportsCollector != null)
            {
                projectImportsCollector.AddFile(filePath);
            }
        }

        private void LogInitialInfo()
        {
            LogMessage("BinLogFilePath=" + FilePath);
            LogMessage("CurrentUICulture=" + System.Globalization.CultureInfo.CurrentUICulture.Name);
        }

        private void LogMessage(string text)
        {
            var args = new BuildMessageEventArgs(text, helpKeyword: null, senderName: "BinaryLogger", MessageImportance.Normal);
            args.BuildEventContext = BuildEventContext.Invalid;
            Write(args);
        }

        /// <summary>
        /// Closes the underlying file stream.
        /// </summary>
        public void Shutdown()
        {
            Environment.SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", _initialTargetOutputLogging);
            Environment.SetEnvironmentVariable("MSBUILDLOGIMPORTS", _initialLogImports);

            if (projectImportsCollector != null)
            {
                projectImportsCollector.Close();

                if (CollectProjectImports == ProjectImportsCollectionMode.Embed)
                {
                    projectImportsCollector.ProcessResult(
                        streamToEmbed => eventArgsWriter.WriteBlob(BinaryLogRecordKind.ProjectImportArchive, streamToEmbed),
                        LogMessage);

                    projectImportsCollector.DeleteArchive();
                }

                projectImportsCollector = null;
            }

            if (stream != null)
            {
                // It's hard to determine whether we're at the end of decoding GZipStream
                // so add an explicit 0 at the end to signify end of file
                stream.WriteByte((byte)BinaryLogRecordKind.EndOfFile);
                stream.Flush();
                stream.Dispose();
                stream = null;
            }
        }

        private void RawEvents_LogDataSliceReceived(BinaryLogRecordKind recordKind, Stream stream)
        {
            eventArgsWriter.WriteBlob(recordKind, stream);
        }

        private void EventSource_AnyEventRaised(object sender, BuildEventArgs e)
        {
            Write(e);
        }

        private void Write(BuildEventArgs e)
        {
            if (stream != null)
            {
                // TODO: think about queuing to avoid contention
                lock (eventArgsWriter)
                {
                    eventArgsWriter.Write(e);
                }

                if (projectImportsCollector != null)
                {
                    CollectImports(e);
                }
            }
        }

        private void CollectImports(BuildEventArgs e)
        {
            if (e is ProjectImportedEventArgs importArgs && importArgs.ImportedProjectFile != null)
            {
                projectImportsCollector.AddFile(importArgs.ImportedProjectFile);
            }
            else if (e is ProjectStartedEventArgs projectArgs)
            {
                projectImportsCollector.AddFile(projectArgs.ProjectFile);
            }
            else if (e is MetaprojectGeneratedEventArgs metaprojectArgs)
            {
                projectImportsCollector.AddFileFromMemory(metaprojectArgs.ProjectFile, metaprojectArgs.metaprojectXml);
            }
        }

        /// <summary>
        /// Processes the parameters given to the logger from MSBuild.
        /// </summary>
        /// <exception cref="LoggerException">
        /// </exception>
        private void ProcessParameters(out bool omitInitialInfo)
        {
            if (Parameters == null)
            {
                throw new LoggerException(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidBinaryLoggerParameters", ""));
            }

            omitInitialInfo = false;
            var parameters = Parameters.Split(MSBuildConstants.SemicolonChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var parameter in parameters)
            {
                if (string.Equals(parameter, "ProjectImports=None", StringComparison.OrdinalIgnoreCase))
                {
                    CollectProjectImports = ProjectImportsCollectionMode.None;
                }
                else if (string.Equals(parameter, "ProjectImports=Embed", StringComparison.OrdinalIgnoreCase))
                {
                    CollectProjectImports = ProjectImportsCollectionMode.Embed;
                }
                else if (string.Equals(parameter, "ProjectImports=ZipFile", StringComparison.OrdinalIgnoreCase))
                {
                    CollectProjectImports = ProjectImportsCollectionMode.ZipFile;
                }
                else if (string.Equals(parameter, "OmitInitialInfo", StringComparison.OrdinalIgnoreCase))
                {
                    omitInitialInfo = true;
                }
                else if (parameter.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase))
                {
                    FilePath = parameter;
                    if (FilePath.StartsWith("LogFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        FilePath = FilePath.Substring("LogFile=".Length);
                    }

                    FilePath = FilePath.Trim('"');
                }
                else
                {
                    throw new LoggerException(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("InvalidBinaryLoggerParameters", parameter));
                }
            }

            if (FilePath == null)
            {
                FilePath = "msbuild.binlog";
            }

            try
            {
                FilePath = Path.GetFullPath(FilePath);
            }
            catch (Exception e)
            {
                string errorCode;
                string helpKeyword;
                string message = ResourceUtilities.FormatResourceStringStripCodeAndKeyword(out errorCode, out helpKeyword, "InvalidFileLoggerFile", FilePath, e.Message);
                throw new LoggerException(message, e, errorCode, helpKeyword);
            }
        }
    }
}
