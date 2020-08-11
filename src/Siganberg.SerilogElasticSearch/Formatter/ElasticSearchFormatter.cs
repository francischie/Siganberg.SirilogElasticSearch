using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Remotion.Linq.Utilities;
using Serilog.Events;
using Serilog.Formatting;
using Siganberg.SerilogElasticSearch.Utilities;

namespace Siganberg.SerilogElasticSearch.Formatter
{
    [ExcludeFromCodeCoverage]
    public class ElasticSearchFormatter : ITextFormatter
    {
        private readonly Dictionary<string, string> _mappings = new Dictionary<string, string>
        {
            {"SourceContext", "callsite"},
            {"RequestMethod", "method"},
            {"Path", "path"},
            {"QueryString", "queryString"},
            {"StatusCode", "responseStatus"},
            {"Elapsed", "durationMs"},
            {"RequestHeaders", "requestHeaders"},
            {"RequestBody", "requestBody"},
            {"ResponseBody", "responseBody"}
        };

        public void Format(LogEvent logEvent, TextWriter output)
        {
            output.Write("{");

            output.Write($"\"time\" : \"{DateTime.Now}\"");
            output.Write($",\"level\" : \"{logEvent.Level.ToString().ToUpper().Replace("INFORMATION", "INFO")}\"");
            WriteCorrelationId(logEvent, output);

            if (logEvent.Exception == null)
                WriteMessage(logEvent, output);
            else
                WriteExceptionIfNonRequestLogging(logEvent, output);

            WriteMappedProperties(logEvent, output);

            output.Write("}");
            output.Write(Environment.NewLine);
        }

        private void WriteMappedProperties(LogEvent logEvent, TextWriter output)
        {
            foreach (var mapping in _mappings)
            {
                if (!logEvent.Properties.ContainsKey(mapping.Key)) continue;
                var p = logEvent.Properties[mapping.Key];
                var keyName = ToCamelCase(mapping.Value);
                object value = p;
                switch (keyName)
                {
                    case "responseStatus":
                        value = AutoCorrectResponseStatus(p);
                        break;
                    case "responseBody":
                        value = CleanBody(p);
                        break;
                }

                output.Write($", \"{keyName}\" : {value}");
            }
        }

        private object CleanBody(LogEventPropertyValue value)
        {
            var result = value.ToString();
            result = result.Replace(@"\\""", "'");
            return result;
        }

        private static void WriteMessage(LogEvent logEvent, TextWriter output)
        {
            if (logEvent.MessageTemplate == null) return;
            var message = logEvent.MessageTemplate.Text;
            var matchVariables = Regex.Matches(logEvent.MessageTemplate.Text, "{.*?}")
                .Select(a => new {Key = a.Value.Replace("{", "").Replace("}", "")?.Split(":").FirstOrDefault(), Expression = a.Value})
                .ToList();

            foreach (var variable in matchVariables)
            {
                if (logEvent.Properties.ContainsKey(variable.Key))
                    message = message.Replace(variable.Expression, logEvent.Properties[variable.Key].ToString().Replace("\"", ""));
            }

            output.Write($", \"message\" : \"{message}\"");
        }

        private static void WriteCorrelationId(LogEvent logEvent, TextWriter output)
        {
            logEvent.Properties.TryGetValue("correlationId", out var correlationValue);
            var correlationId = correlationValue?.ToString();

            if (string.IsNullOrWhiteSpace(correlationId) || correlationId == "null")
                correlationId = StaticHttpContextAccessor.Current?.TraceIdentifier;

            if (!string.IsNullOrWhiteSpace(correlationId))
                output.Write($",\"correlationId\" : \"{correlationId}\"");
        }

        private void WriteExceptionIfNonRequestLogging(LogEvent logEvent, TextWriter output)
        {
            output.Write($", \"message\" : \"{logEvent.Exception.Message}\"");
            if (!logEvent.Properties.ContainsKey("StatusCode"))
            {
                output.Write($", \"exception\" : \"");
                output.Write(logEvent.Exception.ToString().Replace(Environment.NewLine, "\\n"));
                output.Write("\"");
            }
        }

        private object AutoCorrectResponseStatus(LogEventPropertyValue value)
        {
            var statusCode = value.ToString();
            if (statusCode.All(char.IsNumber)) return value;

            Enum.TryParse<HttpStatusCode>(statusCode, out var result);
            return (int) result;
        }

        private string ToCamelCase(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (name.Length == 1) return name.ToLower();
            return name.Substring(0, 1).ToLower() + name.Substring(1);
        }
    }
}