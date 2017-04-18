﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;
using OmniSharp.DotNetTest.Models;
using OmniSharp.DotNetTest.TestFrameworks;
using OmniSharp.Models;
using OmniSharp.Models.Events;
using OmniSharp.Services;

namespace OmniSharp.DotNetTest
{
    internal class VSTestManager : TestManager
    {
        public VSTestManager(Project project, string workingDirectory, DotNetCliService dotNetCli, IEventEmitter eventEmitter, ILoggerFactory loggerFactory)
            : base(project, workingDirectory, dotNetCli, eventEmitter, loggerFactory.CreateLogger<VSTestManager>())
        {
        }

        protected override string GetCliTestArguments(int port, int parentProcessId)
        {
            return $"vstest  --Port:{port} --ParentProcessId:{parentProcessId}";
        }

        protected override void VersionCheck()
        {
            SendMessage(MessageType.VersionCheck, 1);

            var message = ReadMessage();
            var version = message.DeserializePayload<int>();

            if (version != 1)
            {
                throw new InvalidOperationException($"Expected ProtocolVersion 1, but was {version}");
            }
        }

        protected override bool PrepareToConnect()
        {
            // The project must be built before we can test.
            var process = DotNetCli.Start("build", WorkingDirectory);
            process.WaitForExit();

            return File.Exists(Project.OutputFilePath);
        }

        private static void VerifyTestFramework(string testFrameworkName)
        {
            var testFramework = TestFramework.GetFramework(testFrameworkName);
            if (testFramework == null)
            {
                throw new InvalidOperationException($"Unknown test framework: {testFrameworkName}");
            }
        }

        public override GetTestStartInfoResponse GetTestStartInfo(string methodName, string testFrameworkName)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = DiscoverTests(methodName);

            SendMessage(MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new
                {
                    TestCases = testCases,
                    DebuggingEnabled = true
                });

            var message = ReadMessage();
            var testStartInfo = message.DeserializePayload<TestProcessStartInfo>();

            return new GetTestStartInfoResponse
            {
                Executable = testStartInfo.FileName,
                Argument = testStartInfo.Arguments,
                WorkingDirectory = testStartInfo.WorkingDirectory
            };
        }

        public override async Task<DebugTestGetStartInfoResponse> DebugGetStartInfoAsync(string methodName, string testFrameworkName, CancellationToken cancellationToken)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = await DiscoverTestsAsync(methodName, cancellationToken);

            SendMessage(MessageType.GetTestRunnerProcessStartInfoForRunSelected,
                new
                {
                    TestCases = testCases,
                    DebuggingEnabled = true
                });

            var message = await ReadMessageAsync(cancellationToken);
            var startInfo = message.DeserializePayload<TestProcessStartInfo>();

            return new DebugTestGetStartInfoResponse
            {
                FileName = startInfo.FileName,
                Arguments = startInfo.Arguments,
                WorkingDirectory = startInfo.WorkingDirectory,
                EnvironmentVariables = startInfo.EnvironmentVariables
            };
        }

        public override async Task DebugLaunchAsync(CancellationToken cancellationToken)
        {
            SendMessage(MessageType.CustomTestHostLaunchCallback,
                new
                {
                    HostProcessId = Process.GetCurrentProcess().Id
                });

            var done = false;

            while (!done)
            {
                var (success, message) = await TryReadMessageAsync(cancellationToken);
                if (!success)
                {
                    break;
                }

                switch (message.MessageType)
                {
                    case MessageType.TestMessage:
                        var testMessage = message.DeserializePayload<TestMessagePayload>();
                        EventEmitter.Emit(EventTypes.TestMessage,
                            new TestMessageEvent
                            {
                                MessageLevel = testMessage.MessageLevel.ToString().ToLowerInvariant(),
                                Message = testMessage.Message
                            });

                        break;

                    case MessageType.ExecutionComplete:
                        done = true;
                        break;
                }
            }
        }

        public override RunTestResponse RunTest(string methodName, string testFrameworkName)
        {
            VerifyTestFramework(testFrameworkName);

            var testCases = DiscoverTests(methodName);

            var testResults = new List<TestResult>();

            if (testCases.Length > 0)
            {
                // Now, run the tests.
                SendMessage(MessageType.TestRunSelectedTestCasesDefaultHost,
                    new
                    {
                        TestCases = testCases
                    });

                var done = false;

                while (!done)
                {
                    var message = ReadMessage();

                    switch (message.MessageType)
                    {
                        case MessageType.TestMessage:
                            var testMessage = message.DeserializePayload<TestMessagePayload>();
                            EventEmitter.Emit(EventTypes.TestMessage,
                                new TestMessageEvent
                                {
                                    MessageLevel = testMessage.MessageLevel.ToString().ToLowerInvariant(),
                                    Message = testMessage.Message
                                });

                            break;

                        case MessageType.TestRunStatsChange:
                            var testRunChange = message.DeserializePayload<TestRunChangedEventArgs>();

                            testResults.AddRange(testRunChange.NewTestResults);
                            break;

                        case MessageType.ExecutionComplete:
                            var payload = message.DeserializePayload<TestRunCompletePayload>();

                            done = true;
                            break;
                    }
                }
            }

            var results = testResults.Select(testResult =>
                new DotNetTestResult
                {
                    MethodName = testResult.TestCase.FullyQualifiedName,
                    Outcome = testResult.Outcome.ToString().ToLowerInvariant(),
                    ErrorMessage = testResult.ErrorMessage,
                    ErrorStackTrace = testResult.ErrorStackTrace
                });

            return new RunTestResponse
            {
                Results = results.ToArray(),
                Pass = !testResults.Any(r => r.Outcome == TestOutcome.Failed)
            };
        }

        private async Task<TestCase[]> DiscoverTestsAsync(string methodName, CancellationToken cancellationToken)
        {
            SendMessage(MessageType.StartDiscovery,
                new
                {
                    Sources = new[]
                    {
                        Project.OutputFilePath
                    }
                });

            var testCases = new List<TestCase>();
            var done = false;

            while (!done)
            {
                var (success, message) = await TryReadMessageAsync(cancellationToken);
                if (!success)
                {
                    return Array.Empty<TestCase>();
                }

                switch (message.MessageType)
                {
                    case MessageType.TestMessage:
                        break;

                    case MessageType.TestCasesFound:
                        foreach (var testCase in message.DeserializePayload<TestCase[]>())
                        {
                            if (testCase.DisplayName.StartsWith(methodName))
                            {
                                testCases.Add(testCase);
                            }
                        }

                        break;

                    case MessageType.DiscoveryComplete:
                        done = true;
                        break;
                }
            }

            return testCases.ToArray();
        }

        private TestCase[] DiscoverTests(string methodName)
        {
            return DiscoverTestsAsync(methodName, CancellationToken.None).Result;
        }
    }
}