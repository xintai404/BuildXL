﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Linq;
using System.Text;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Utilities;

namespace BuildXL.Cache.ContentStore.Logging
{
    /// <summary>
    ///     Like <see cref="FileLog"/> except that it produces a valid CSV file.
    /// </summary>
    public sealed class CsvFileLog : FileLog
    {
        /// <summary>
        ///     Types of supported columns for the output CSV file
        /// </summary>
        public enum ColumnKind
        {
            /// <summary>
            ///     Empty string.
            /// </summary>
            Empty,

            /// <summary>
            ///     A GUID that is either explicitly provided or autogenerated by the log object (<see cref="CsvFileLog.BuildId"/>)
            /// </summary>
            BuildId,

            /// <summary>
            ///     Name of the host machine
            /// </summary>
            Machine,

            /// <summary>
            ///     Timestamp of the message in UTC.
            /// </summary>
            PreciseTimeStamp,

            /// <summary>
            ///     The id of the thread logging the message
            /// </summary>
            ThreadId,

            /// <summary>
            ///     The id of the process logging the message
            /// </summary>
            ProcessId,

            /// <summary>
            ///     Ordinal representation of <see cref="Severity"/>
            /// </summary>
            LogLevel,

            /// <summary>
            ///     Friendly string representation of <see cref="Severity"/>
            /// </summary>
            LogLevelFriendly,

            /// <summary>
            ///     Name of the service using this log (service name is passed in the constructor of <see cref="CsvFileLog"/>)
            /// </summary>
            Service,

            /// <summary>
            ///     Message to log
            /// </summary>
            Message,

            /// <summary>
            ///     Operating system platform (<see cref="OperatingSystem.Platform"/>)
            /// </summary>
            env_os,

            /// <summary>
            ///     Operating system version (<see cref="OperatingSystem.Version"/>)
            /// </summary>
            env_osVer
        }

        // case-insensitive mapping of ColumKind enum name to enum value
        private static Dictionary<string, ColumnKind> ColumnName2ValueMap = typeof(ColumnKind)
            .GetEnumNames()
            .ToDictionary
                (
                enumName => enumName,
                enumName => (ColumnKind)Enum.Parse(typeof(ColumnKind), enumName),
                StringComparer.OrdinalIgnoreCase
                );

        private readonly Dictionary<ColumnKind, string> _constColumns;

        /// <summary>
        ///     Schema of the produced CSV file.
        /// </summary>
        public IReadOnlyList<ColumnKind> FileSchema { get; }

        /// <summary>
        ///     Additional columns that are conceptually part of the schema but are not rendered to the
        ///     output CSV file because their values remain constant throughout the execution of this log. 
        ///     These columns are not included in <see cref="FileSchema"/>.
        /// </summary>
        public IReadOnlyList<ColumnKind> ConstSchema { get; }

        /// <summary>
        ///     Unique identifier of this log object.
        /// </summary>
        public Guid BuildId { get; }

        /// <summary>
        ///     Parses a string-formatted table name and returns a corresponding array of <see cref="ColumnKind"/>.
        ///
        ///     The expected string format is:
        ///
        ///         ColName[:ColType](, ColName[:ColType])*
        ///
        ///     ColName is a string identifier, i.e., any string that doesn't contain either ',' or ':'.
        ///
        ///     ColType is ignored.
        ///
        ///     Each ColName is mapped to a <see cref="ColumnKind"/> enum constant.  The value of ColName is
        ///     first matched against the names of <see cref="ColumnKind"/> enum constants (using the
        ///     case-insensitive string comparer).  If a match is found, the value is mapped to that enum
        ///     constant; otherwise, it is mapped to <see cref="ColumnKind.Empty"/>
        /// </summary>
        public static ColumnKind[] ParseTableSchema(string kustoTableSchema)
        {
            if (string.IsNullOrWhiteSpace(kustoTableSchema))
            {
                return new ColumnKind[0];
            }

            return kustoTableSchema.Trim()
                .Split(',')
                .Select(col => col.Split(':').First().Trim())
                .Select(colName => GetValueOrDefault(ColumnName2ValueMap, colName, ColumnKind.Empty))
                .ToArray();
        }

        private static TV GetValueOrDefault<TK, TV>(Dictionary<TK, TV> dict, TK key, TV defaultValue)
        {
            return dict.TryGetValue(key, out var value)
                ? value
                : defaultValue;
        }

        /// <summary>
        ///     Whether the value of <paramref name="col"/> is constant over time.
        ///
        ///     For example, <code>true</code> is returned for columns <see cref="ColumnKind.Empty"/>,
        ///     <see cref="ColumnKind.BuildId"/>, <see cref="ColumnKind.Machine"/> etc., because their values
        ///     don't change during a single execution of the program; in contrast, columns like
        ///     <see cref="ColumnKind.Message"/>, <see cref="ColumnKind.PreciseTimeStamp"/> are not constant.
        /// </summary>
        public bool IsConstValueColumn(ColumnKind col) => _constColumns.ContainsKey(col);

        /// <summary>
        ///     Constructor.  Initializes this object and does nothing else.
        /// </summary>
        /// <param name="logFilePath">Full path to log file</param>
        /// <param name="schema">CSV schema as a list of columns. Each element in the list denotes a column to be rendered at that position.</param>
        /// <param name="renderConstColums">
        ///     When false, const columns (<see cref="IsConstValueColumn"/>) from <paramref name="schema"/> are not
        ///     rendered to log file (those columns become available through the <see ref="ConstSchema"/> property.
        /// </param>
        /// <param name="severity">Minimum severity to log</param>
        /// <param name="maxFileSize">Maximum size of the log file.</param>
        /// <param name="serviceName">Name of the service using this log.  Used to render <see cref="ColumnKind.Service"/></param>
        /// <param name="buildId">A uniqu build identifier.  Used to render <see cref="ColumnKind.BuildId"/></param>
        public CsvFileLog
            (
            string logFilePath,
            IEnumerable<ColumnKind> schema,
            bool renderConstColums = true,
            Severity severity = Severity.Diagnostic,
            long maxFileSize = 0,
            string serviceName = null,
            Guid buildId = default
            )
            :
            base
                (
                logFilePath,
                severity,
                autoFlush: true,
                maxFileSize: maxFileSize,
                maxFileCount: 0 // unlimited
                )
        {
            Contract.Requires(schema != null);

            BuildId = buildId == default ? Guid.NewGuid() : buildId;

            _constColumns = new Dictionary<ColumnKind, string>
            {
                [ColumnKind.Empty] = string.Empty,
                [ColumnKind.BuildId] = BuildId.ToString(),
                [ColumnKind.Machine] = Environment.MachineName,
                [ColumnKind.Service] = serviceName ?? string.Empty,
                [ColumnKind.env_os]    = Environment.OSVersion.Platform.ToString(),
                [ColumnKind.env_osVer] = Environment.OSVersion.Version.ToString()
            };

            if (renderConstColums)
            {
                FileSchema = schema.ToArray();
                ConstSchema = new ColumnKind[0];
            }
            else
            {
                var groupedByIsConst = schema
                    .GroupBy(col => IsConstValueColumn(col))
                    .ToDictionary(grp => grp.Key, grp => grp.ToArray());
                FileSchema = GetValueOrDefault(groupedByIsConst, key: false, defaultValue: new ColumnKind[0]);
                ConstSchema = GetValueOrDefault(groupedByIsConst, key: true, defaultValue: new ColumnKind[0]);
            }
        }

        /// <summary>
        ///     Logs a message to the underlying CSV file according to the schema passed to the constructor of this object.
        /// </summary>
        public override void Write(DateTime dateTime, int threadId, Severity severity, string message)
        {
            if (severity < CurrentSeverity)
            {
                return;
            }

            using (var stringBuilderPool = Pools.StringBuilderPool.GetInstance())
            {
                StringBuilder line = stringBuilderPool.Instance;
                RenderMessage(line, dateTime, threadId, severity, message);
                WriteLineInternal(severity, line.ToString());
            }
        }

        /// <nodoc />
        public void RenderMessage(StringBuilder line, DateTime dateTime, int threadId, Severity severity, string message)
        {
            foreach (var col in FileSchema)
            {
                if (line.Length > 0)
                {
                    line.Append(",");
                }

                line.Append(CsvEscape(RenderColumn(col, dateTime, threadId, severity, message)));
            }
        }

        /// <nodoc />
        public string RenderColumn(ColumnKind col, DateTime dateTime, int threadId, Severity severity, string message)
        {
            if (_constColumns.TryGetValue(col, out var constColumnValue))
            {
                return constColumnValue;
            }

            switch (col)
            {
                case ColumnKind.PreciseTimeStamp:
                    return FormatTimeStamp(dateTime.ToUniversalTime());
                case ColumnKind.ThreadId:
                    return threadId.ToString();
                case ColumnKind.ProcessId:
                    return System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                case ColumnKind.LogLevel:
                    return ((int)severity).ToString();
                case ColumnKind.LogLevelFriendly:
                    return severity.ToString();
                case ColumnKind.Message:
                    return message;
                default:
                    throw Contract.AssertFailure("Unknown column type: " + col);
            }
        }

        /// <summary>
        ///     Returns the const value of the <paramref name="col"/> column.
        ///     Precondition: <see cref="IsConstValueColumn"/> must return true for <paramref name="col"/>.
        /// </summary>
        public string RenderConstColumn(ColumnKind col)
        {
            Contract.Requires(IsConstValueColumn(col));
            return _constColumns[col];
        }

        private string FormatTimeStamp(DateTime dateTime)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff}", dateTime);
        }

        private string CsvEscape(string str)
        {
            return '"' + str.Replace("\"", "\"\"") + '"';
        }
    }
}