﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Host.Loggers;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Host.UnitTests.Loggers
{
    public class ApplicationInsightsLoggerTests
    {
        private readonly Guid _invocationId = Guid.NewGuid();
        private readonly Guid _hostInstanceId = Guid.NewGuid();
        private readonly DateTime _startTime = DateTime.UtcNow;
        private readonly DateTime _endTime;
        private readonly string _triggerReason = "new queue message";
        private readonly string _functionFullName = "Functions.TestFunction";
        private readonly string _functionShortName = "TestFunction";
        private readonly string _functionCategoryName;
        private readonly IDictionary<string, string> _arguments;
        private readonly TestTelemetryChannel _channel = new TestTelemetryChannel();
        private readonly string defaultIp = "0.0.0.0";
        private readonly TelemetryClient _client;
        private readonly int _durationMs = 450;
        private readonly IFunctionInstance _functionInstance;

        public ApplicationInsightsLoggerTests()
        {
            _functionCategoryName = LogCategories.CreateFunctionUserCategory(_functionShortName);
            _endTime = _startTime.AddMilliseconds(_durationMs);
            _arguments = new Dictionary<string, string>
            {
                ["queueMessage"] = "my message",
                ["anotherParam"] = "some value"
            };

            TelemetryConfiguration config = new TelemetryConfiguration
            {
                TelemetryChannel = _channel,
                InstrumentationKey = "some key"
            };

            // Add the same initializers that we use in the product code
            DefaultTelemetryClientFactory.AddInitializers(config);

            _client = new TelemetryClient(config);

            var descriptor = new FunctionDescriptor
            {
                FullName = _functionFullName,
                ShortName = _functionShortName
            };

            _functionInstance = new FunctionInstance(_invocationId, null, ExecutionReason.AutomaticTrigger, null, null, descriptor);
        }

        [Fact]
        public async Task LogFunctionResult_Succeeded_SendsCorrectTelemetry()
        {
            var result = CreateDefaultInstanceLogEntry();
            ILogger logger = CreateLogger(LogCategories.Results);

            using (logger.BeginFunctionScope(CreateFunctionInstance(_invocationId), _hostInstanceId))
            {
                // sleep briefly to provide a non-zero Duration
                await Task.Delay(100);
                logger.LogFunctionResult(result);
            }

            RequestTelemetry telemetry = _channel.Telemetries.Single() as RequestTelemetry;

            Assert.Equal(_invocationId.ToString(), telemetry.Id);
            Assert.Equal(_invocationId.ToString(), telemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, telemetry.Name);
            Assert.Equal(_functionShortName, telemetry.Context.Operation.Name);
            Assert.True(telemetry.Duration > TimeSpan.Zero, "Expected a non-zero Duration.");
            Assert.Equal(defaultIp, telemetry.Context.Location.Ip);
            Assert.Equal(LogCategories.Results, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Information.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal(_triggerReason, telemetry.Properties[LogConstants.TriggerReasonKey]);
            // TODO: Beef up validation to include properties
        }

        [Fact]
        public void LogFunctionResult_Failed_SendsCorrectTelemetry()
        {
            FunctionInvocationException fex = new FunctionInvocationException("Failed");
            var result = CreateDefaultInstanceLogEntry(fex);
            ILogger logger = CreateLogger(LogCategories.Results);

            using (logger.BeginFunctionScope(CreateFunctionInstance(_invocationId), _hostInstanceId))
            {
                logger.LogFunctionResult(result);
            }

            // Errors log an associated Exception
            RequestTelemetry requestTelemetry = _channel.Telemetries.OfType<RequestTelemetry>().Single();
            ExceptionTelemetry exceptionTelemetry = _channel.Telemetries.OfType<ExceptionTelemetry>().Single();

            Assert.Equal(2, _channel.Telemetries.Count);
            Assert.Equal(_invocationId.ToString(), requestTelemetry.Id);
            Assert.Equal(_invocationId.ToString(), requestTelemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, requestTelemetry.Name);
            Assert.Equal(_functionShortName, requestTelemetry.Context.Operation.Name);
            Assert.Equal(defaultIp, requestTelemetry.Context.Location.Ip);
            Assert.Equal(LogCategories.Results, requestTelemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Error.ToString(), requestTelemetry.Properties[LogConstants.LogLevelKey]);
            // TODO: Beef up validation to include properties

            // Starting the telemetry prefixes/postfixes values to the request id, but the original guid is there
            Assert.Equal(_invocationId.ToString(), requestTelemetry.Id);

            // Exception needs to have associated id
            Assert.Equal(_invocationId.ToString(), exceptionTelemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, exceptionTelemetry.Context.Operation.Name);
            Assert.Same(fex, exceptionTelemetry.Exception);
            Assert.Equal(LogCategories.Results, exceptionTelemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Error.ToString(), exceptionTelemetry.Properties[LogConstants.LogLevelKey]);
            // TODO: Beef up validation to include properties
        }

        [Fact]
        public void LogFunctionAggregate_SendsCorrectTelemetry()
        {
            DateTime now = DateTime.UtcNow;
            var resultAggregate = new FunctionResultAggregate
            {
                Name = _functionFullName,
                Failures = 4,
                Successes = 116,
                MinDuration = TimeSpan.FromMilliseconds(200),
                MaxDuration = TimeSpan.FromMilliseconds(2180),
                AverageDuration = TimeSpan.FromMilliseconds(340),
                Timestamp = now
            };

            ILogger logger = CreateLogger(LogCategories.Aggregator);
            logger.LogFunctionResultAggregate(resultAggregate);

            IEnumerable<MetricTelemetry> metrics = _channel.Telemetries.Cast<MetricTelemetry>();
            // turn them into a dictionary so we can easily validate
            IDictionary<string, MetricTelemetry> metricDict = metrics.ToDictionary(m => m.Name, m => m);

            Assert.Equal(7, metricDict.Count);

            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.FailuresKey}"], 4, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.SuccessesKey}"], 116, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.MinDurationKey}"], 200, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.MaxDurationKey}"], 2180, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.AverageDurationKey}"], 340, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.SuccessRateKey}"], 96.67, LogLevel.Information);
            ValidateMetric(metricDict[$"{_functionFullName} {LogConstants.CountKey}"], 120, LogLevel.Information);
        }

        private static void ValidateMetric(MetricTelemetry metric, double expectedValue, LogLevel expectedLevel, string expectedCategory = LogCategories.Aggregator)
        {
            Assert.Equal(expectedValue, metric.Value);
            Assert.Equal(2, metric.Properties.Count);
            Assert.Equal(expectedCategory, metric.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(expectedLevel.ToString(), metric.Properties[LogConstants.LogLevelKey]);
        }

        [Fact]
        public void Log_NoProperties_CreatesTraceAndCorrelates()
        {
            Guid scopeGuid = Guid.NewGuid();

            ILogger logger = CreateLogger(_functionCategoryName);
            using (logger.BeginFunctionScope(CreateFunctionInstance(scopeGuid), _hostInstanceId))
            {
                logger.LogInformation("Information");
                logger.LogCritical("Critical");
                logger.LogDebug("Debug");
                logger.LogError("Error");
                logger.LogTrace("Trace");
                logger.LogWarning("Warning");
            }

            Assert.Equal(6, _channel.Telemetries.Count);
            Assert.Equal(6, _channel.Telemetries.OfType<TraceTelemetry>().Count());
            foreach (var telemetry in _channel.Telemetries.Cast<TraceTelemetry>())
            {
                Enum.TryParse(telemetry.Message, out LogLevel expectedLogLevel);
                Assert.Equal(expectedLogLevel.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);

                SeverityLevel expectedSeverityLevel;
                if (telemetry.Message == "Trace" || telemetry.Message == "Debug")
                {
                    expectedSeverityLevel = SeverityLevel.Verbose;
                }
                else
                {
                    Assert.True(Enum.TryParse(telemetry.Message, out expectedSeverityLevel));
                }
                Assert.Equal(expectedSeverityLevel, telemetry.SeverityLevel);

                Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
                Assert.Equal(telemetry.Message, telemetry.Properties[LogConstants.CustomPropertyPrefix + LogConstants.OriginalFormatKey]);
                Assert.Equal(scopeGuid.ToString(), telemetry.Context.Operation.Id);
                Assert.Equal(_functionShortName, telemetry.Context.Operation.Name);
            }
        }

        [Fact]
        public void Log_WithProperties_IncludesProps()
        {
            ILogger logger = CreateLogger(_functionCategoryName);
            logger.LogInformation("Using {some} custom {properties}. {Test}.", "1", 2, "3");

            var telemetry = _channel.Telemetries.Single() as TraceTelemetry;

            Assert.Equal(SeverityLevel.Information, telemetry.SeverityLevel);

            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Information.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal("Using {some} custom {properties}. {Test}.",
                telemetry.Properties[LogConstants.CustomPropertyPrefix + LogConstants.OriginalFormatKey]);
            Assert.Equal("Using 1 custom 2. 3.", telemetry.Message);
            Assert.Equal("1", telemetry.Properties[LogConstants.CustomPropertyPrefix + "some"]);
            Assert.Equal("2", telemetry.Properties[LogConstants.CustomPropertyPrefix + "properties"]);
            Assert.Equal("3", telemetry.Properties[LogConstants.CustomPropertyPrefix + "Test"]);
        }

        [Fact]
        public void Log_WithException_CreatesExceptionAndCorrelates()
        {
            var ex = new InvalidOperationException("Failure");
            Guid scopeGuid = Guid.NewGuid();
            ILogger logger = CreateLogger(_functionCategoryName);

            using (logger.BeginFunctionScope(CreateFunctionInstance(scopeGuid), _hostInstanceId))
            {
                logger.LogError(0, ex, "Error with customer: {customer}.", "John Doe");
            }

            Assert.Equal(2, _channel.Telemetries.Count());
            var exceptionTelemetry = _channel.Telemetries.OfType<ExceptionTelemetry>().Single();
            var traceTelemetry = _channel.Telemetries.OfType<TraceTelemetry>().Single();

            Assert.Equal(SeverityLevel.Error, exceptionTelemetry.SeverityLevel);

            Assert.Equal(_functionCategoryName, exceptionTelemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Error.ToString(), exceptionTelemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal("Error with customer: {customer}.", exceptionTelemetry.Properties[LogConstants.CustomPropertyPrefix + LogConstants.OriginalFormatKey]);
            Assert.Equal("Error with customer: John Doe.", exceptionTelemetry.Properties[LogConstants.FormattedMessageKey]);
            Assert.Equal("John Doe", exceptionTelemetry.Properties[LogConstants.CustomPropertyPrefix + "customer"]);
            Assert.Same(ex, exceptionTelemetry.Exception);
            Assert.Equal(scopeGuid.ToString(), exceptionTelemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, exceptionTelemetry.Context.Operation.Name);

            string internalMessage = GetInternalExceptionMessages(exceptionTelemetry).Single();
            Assert.Equal("Failure", internalMessage);

            // We should not have the request logged.
            Assert.False(exceptionTelemetry.Properties.TryGetValue(LogConstants.CustomPropertyPrefix + ApplicationInsightsScopeKeys.HttpRequest, out string request));
        }

        [Fact]
        public void LogMetric_NoProperties()
        {
            ILogger logger = CreateLogger(_functionCategoryName);
            Guid scopeGuid = Guid.NewGuid();

            using (logger.BeginFunctionScope(CreateFunctionInstance(scopeGuid), _hostInstanceId))
            {
                logger.LogMetric("CustomMetric", 44.9);
            }

            var telemetry = _channel.Telemetries.Single() as MetricTelemetry;

            Assert.Equal(2, telemetry.Properties.Count);
            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Information.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);

            Assert.Equal("CustomMetric", telemetry.Name);
            Assert.Equal(44.9, telemetry.Sum);
            Assert.Equal(scopeGuid.ToString(), telemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, telemetry.Context.Operation.Name);

            Assert.Null(telemetry.Min);
            Assert.Null(telemetry.Max);
            Assert.Equal(1, telemetry.Count);
            Assert.Null(telemetry.StandardDeviation);
        }

        [Fact]
        public void LogMetric_AllProperties()
        {
            ILogger logger = CreateLogger(_functionCategoryName);
            Guid scopeGuid = Guid.NewGuid();

            using (logger.BeginFunctionScope(CreateFunctionInstance(scopeGuid), _hostInstanceId))
            {
                var props = new Dictionary<string, object>
                {
                    ["MyCustomProp1"] = "abc",
                    ["MyCustomProp2"] = "def",
                    ["Count"] = 2,
                    ["Min"] = 3.3,
                    ["Max"] = 4.4,
                    ["StandardDeviation"] = 5.5
                };
                logger.LogMetric("CustomMetric", 1.1, props);
            }

            var telemetry = _channel.Telemetries.Single() as MetricTelemetry;

            Assert.Equal(4, telemetry.Properties.Count);
            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Information.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal("abc", telemetry.Properties[$"{LogConstants.CustomPropertyPrefix}MyCustomProp1"]);
            Assert.Equal("def", telemetry.Properties[$"{LogConstants.CustomPropertyPrefix}MyCustomProp2"]);

            Assert.Equal(scopeGuid.ToString(), telemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, telemetry.Context.Operation.Name);

            Assert.Equal("CustomMetric", telemetry.Name);
            Assert.Equal(1.1, telemetry.Sum);
            Assert.Equal(2, telemetry.Count);
            Assert.Equal(3.3, telemetry.Min);
            Assert.Equal(4.4, telemetry.Max);
            Assert.Equal(5.5, telemetry.StandardDeviation);
        }

        [Fact]
        public void LogMetric_AllProperties_Lowercase()
        {
            ILogger logger = CreateLogger(_functionCategoryName);
            Guid scopeGuid = Guid.NewGuid();

            using (logger.BeginFunctionScope(CreateFunctionInstance(scopeGuid), _hostInstanceId))
            {
                var props = new Dictionary<string, object>
                {
                    ["MyCustomProp1"] = "abc",
                    ["MyCustomProp2"] = "def",
                    ["count"] = 2,
                    ["min"] = 3.3,
                    ["max"] = 4.4,
                    ["standardDeviation"] = 5.5
                };
                logger.LogMetric("CustomMetric", 1.1, props);
            }

            var telemetry = _channel.Telemetries.Single() as MetricTelemetry;

            Assert.Equal(4, telemetry.Properties.Count);
            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Information.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal("abc", telemetry.Properties[$"{LogConstants.CustomPropertyPrefix}MyCustomProp1"]);
            Assert.Equal("def", telemetry.Properties[$"{LogConstants.CustomPropertyPrefix}MyCustomProp2"]);

            Assert.Equal(scopeGuid.ToString(), telemetry.Context.Operation.Id);
            Assert.Equal(_functionShortName, telemetry.Context.Operation.Name);

            Assert.Equal("CustomMetric", telemetry.Name);
            Assert.Equal(1.1, telemetry.Sum);
            Assert.Equal(2, telemetry.Count);
            Assert.Equal(3.3, telemetry.Min);
            Assert.Equal(4.4, telemetry.Max);
            Assert.Equal(5.5, telemetry.StandardDeviation);
        }

        [Theory]
        [InlineData("1.2.3.4:5")]
        [InlineData("1.2.3.4")]
        public void GetIpAddress_ChecksHeaderFirst(string headerIp)
        {
            var request = new Mock<HttpRequest>();
            var headers = new HeaderDictionary
            {
                { ApplicationInsightsScopeKeys.ForwardedForHeaderName, headerIp }
            };
            request.SetupGet(r => r.Headers).Returns(headers);

            MockHttpRequest(request, "5.6.7.8");

            string ip = ApplicationInsightsLogger.GetIpAddress(request.Object);

            Assert.Equal("1.2.3.4", ip);
        }

        [Fact]
        public void GetIpAddress_ChecksContextSecond()
        {
            var request = new Mock<HttpRequest>();

            MockHttpRequest(request, "5.6.7.8");

            string ip = ApplicationInsightsLogger.GetIpAddress(request.Object);

            Assert.Equal("5.6.7.8", ip);
        }

        [Fact]
        public void Log_AcceptsStringsAsState()
        {
            var logger = CreateLogger(_functionCategoryName);
            logger.Log(LogLevel.Information, 0, "some string", null, (s, e) => s.ToString());

            var telemetry = _channel.Telemetries.Single() as TraceTelemetry;
            Assert.Equal("some string", telemetry.Message);
            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
        }

        [Fact]
        public void Log_Exception_NoLogMessage()
        {
            var logger = CreateLogger(_functionCategoryName);
            var innerEx = new Exception("Inner");
            var outerEx = new Exception("Outer", innerEx);

            logger.LogError(0, outerEx, string.Empty);

            var telemetry = _channel.Telemetries.Single() as ExceptionTelemetry;

            string[] internalMessages = GetInternalExceptionMessages(telemetry).ToArray();

            Assert.Equal(2, internalMessages.Length);
            Assert.Equal("Outer", internalMessages[0]);
            Assert.Equal("Inner", internalMessages[1]);

            Assert.Equal(outerEx, telemetry.Exception);
            Assert.Equal(_functionCategoryName, telemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Error.ToString(), telemetry.Properties[LogConstants.LogLevelKey]);
            Assert.DoesNotContain(LogConstants.FormattedMessageKey, telemetry.Properties.Keys);
        }

        [Fact]
        public void Log_Exception_LogMessage()
        {
            var logger = CreateLogger(_functionCategoryName);
            var innerEx = new Exception("Inner");
            var outerEx = new Exception("Outer", innerEx);

            logger.LogError(0, outerEx, "Log message");

            Assert.Equal(2, _channel.Telemetries.Count());
            var exceptionTelemetry = _channel.Telemetries.OfType<ExceptionTelemetry>().Single();
            var traceTelemetry = _channel.Telemetries.OfType<TraceTelemetry>().Single();

            string[] internalMessages = GetInternalExceptionMessages(exceptionTelemetry).ToArray();

            Assert.Equal(2, internalMessages.Length);
            Assert.Equal("Outer", internalMessages[0]);
            Assert.Equal("Inner", internalMessages[1]);

            Assert.Equal(outerEx, exceptionTelemetry.Exception);
            Assert.Equal(_functionCategoryName, exceptionTelemetry.Properties[LogConstants.CategoryNameKey]);
            Assert.Equal(LogLevel.Error.ToString(), exceptionTelemetry.Properties[LogConstants.LogLevelKey]);
            Assert.Equal("Log message", exceptionTelemetry.Properties[LogConstants.FormattedMessageKey]);
        }

        private static IEnumerable<string> GetInternalExceptionMessages(ExceptionTelemetry telemetry)
        {
            IList<string> internalMessages = new List<string>();

            // The transmitted details may get out-of-sync with the Exception. We previously had bugs 
            // around this, so double-checking that the exception messages remain as intended. These are 
            // all internal to App Insights so pull them out with reflection.
            PropertyInfo exceptionsProp = typeof(ExceptionTelemetry).GetProperty("Exceptions", BindingFlags.NonPublic | BindingFlags.Instance);
            var details = exceptionsProp.GetValue(telemetry) as IEnumerable<object>;

            foreach (var detail in details)
            {
                var messageProp = detail.GetType().GetProperty("message", BindingFlags.Public | BindingFlags.Instance);
                internalMessages.Add(messageProp.GetValue(detail) as string);
            }

            return internalMessages;
        }

        [Fact]
        public async Task BeginScope()
        {
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Level1(Guid.NewGuid()));
            }

            await Task.WhenAll(tasks);
        }

        private async Task Level1(Guid asyncLocalSetting)
        {
            // Push and pop values onto the dictionary at various levels. Make sure they
            // maintain their AsyncLocal state
            var level1 = new Dictionary<string, object>
            {
                ["AsyncLocal"] = asyncLocalSetting,
                ["1"] = 1
            };

            ILogger logger = CreateLogger(_functionCategoryName);
            using (logger.BeginScope(level1))
            {
                ValidateScope(level1);

                await Level2(asyncLocalSetting);

                ValidateScope(level1);
            }
        }

        private async Task Level2(Guid asyncLocalSetting)
        {
            await Task.Delay(1);

            var level2 = new Dictionary<string, object>
            {
                ["2"] = 2
            };

            var expectedLevel2 = new Dictionary<string, object>
            {
                ["1"] = 1,
                ["2"] = 2,
                ["AsyncLocal"] = asyncLocalSetting
            };

            ILogger logger2 = CreateLogger(_functionCategoryName);
            using (logger2.BeginScope(level2))
            {
                ValidateScope(expectedLevel2);

                await Level3(asyncLocalSetting);

                ValidateScope(expectedLevel2);
            }
        }

        private async Task Level3(Guid asyncLocalSetting)
        {
            await Task.Delay(1);

            // also overwrite value 1, we expect this to win here
            var level3 = new Dictionary<string, object>
            {
                ["1"] = 11,
                ["3"] = 3
            };

            var expectedLevel3 = new Dictionary<string, object>
            {
                ["1"] = 11,
                ["2"] = 2,
                ["3"] = 3,
                ["AsyncLocal"] = asyncLocalSetting
            };

            ILogger logger3 = CreateLogger(_functionCategoryName);
            using (logger3.BeginScope(level3))
            {
                ValidateScope(expectedLevel3);
            }
        }

        private static void MockHttpRequest(Mock<HttpRequest> request, string ipAddress, IDictionary<object, object> items = null)
        {
            var connectionInfoMock = new Mock<ConnectionInfo>();
            connectionInfoMock.SetupGet(c => c.RemoteIpAddress).Returns(IPAddress.Parse(ipAddress));

            var contextMock = new Mock<HttpContext>();
            contextMock.SetupGet(c => c.Connection).Returns(connectionInfoMock.Object);

            request.SetupGet(r => r.HttpContext).Returns(contextMock.Object);

            items = items ?? new Dictionary<object, object>();
            contextMock.SetupGet(c => c.Items).Returns(items);
        }

        private ILogger CreateLogger(string category)
        {
            return new ApplicationInsightsLogger(_client, category);
        }

        private static void ValidateScope(IDictionary<string, object> expected)
        {
            var scopeDict = DictionaryLoggerScope.GetMergedStateDictionary();
            Assert.Equal(expected.Count, scopeDict.Count);
            foreach (var entry in expected)
            {
                Assert.Equal(entry.Value, scopeDict[entry.Key]);
            }
        }

        private IFunctionInstance CreateFunctionInstance(Guid id)
        {
            var method = GetType().GetMethod(nameof(TestFunction), BindingFlags.NonPublic | BindingFlags.Static);
            var descriptor = FunctionIndexer.FromMethod(method);

            return new FunctionInstance(id, null, new ExecutionReason(), null, null, descriptor);
        }

        private static IDictionary<string, object> CreateScopeDictionary(string invocationId, string functionName)
        {
            return new Dictionary<string, object>
            {
                [ScopeKeys.FunctionInvocationId] = invocationId,
                [ScopeKeys.FunctionName] = functionName
            };
        }

        private static void TestFunction()
        {
            // used for a FunctionDescriptor
        }

        private FunctionInstanceLogEntry CreateDefaultInstanceLogEntry(Exception ex = null)
        {
            return new FunctionInstanceLogEntry
            {
                FunctionName = _functionFullName,
                LogName = _functionShortName,
                FunctionInstanceId = _invocationId,
                StartTime = _startTime,
                EndTime = _endTime,
                LogOutput = "a bunch of output that we will not forward", // not used here -- this is all Traced
                TriggerReason = _triggerReason,
                ParentId = Guid.NewGuid(), // we do not track this
                ErrorDetails = null, // we do not use this -- we pass the exception in separately
                Arguments = _arguments,
                Duration = TimeSpan.FromMilliseconds(_durationMs),
                Exception = ex
            };
        }
    }
}
