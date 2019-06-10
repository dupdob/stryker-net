﻿using Microsoft.Extensions.Logging;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer;
using Microsoft.TestPlatform.VsTestConsole.TranslationLayer.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Serilog.Events;
using Stryker.Core.Initialisation;
using Stryker.Core.Logging;
using Stryker.Core.Options;
using Stryker.Core.ToolHelpers;
using Stryker.DataCollector;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Stryker.Core.TestRunners.VsTest
{
    public class VsTestRunner : ITestRunner
    {
        private readonly IFileSystem _fileSystem;
        private readonly StrykerOptions _options;
        private readonly OptimizationFlags _flags;
        private readonly ProjectInfo _projectInfo;

        private readonly IVsTestConsoleWrapper _vsTestConsole;
        private ICollection<TestCase> _discoveredTests;

        private readonly VsTestHelper _vsTestHelper;
        private readonly List<string> _messages = new List<string>();
        private readonly Dictionary<string, string> _coverageEnvironment;

        private IEnumerable<string> _sources;
        private bool _disposedValue; // To detect redundant calls

        private static ILogger Logger { get; }

        static VsTestRunner()
        {
            Logger = ApplicationLogging.LoggerFactory.CreateLogger<VsTestRunner>();
        }

        public VsTestRunner(StrykerOptions options, OptimizationFlags flags, ProjectInfo projectInfo,
            ICollection<TestCase> testCasesDiscovered,
            TestCoverageInfos mappingInfos, IFileSystem fileSystem = null)
        {
            _fileSystem = fileSystem ?? new FileSystem();
            _options = options;
            _flags = flags;
            _projectInfo = projectInfo;
            SetListOfTests(testCasesDiscovered);
            _vsTestHelper = new VsTestHelper();
            CoverageMutants = mappingInfos ?? new TestCoverageInfos();
            _vsTestConsole = PrepareVsTestConsole();
            InitializeVsTestConsole();
            _coverageEnvironment = new Dictionary<string, string>
            {
                {CoverageCollector.ModeEnvironmentVariable, flags.HasFlag(OptimizationFlags.UseEnvVariable) ? CoverageCollector.EnvMode : CoverageCollector.PipeMode}
            };
        }

        public IEnumerable<int> CoveredMutants { get; private set; }

        public TestCoverageInfos CoverageMutants {get;}

        public TestRunResult RunAll(int? timeoutMs, int? mutationId)
        {
            var envVars = new Dictionary<string, string>();
            if (mutationId != null)
            {
                envVars["ActiveMutation"] = mutationId.ToString();
            }

            var testCases = (mutationId == null || !_flags.HasFlag(OptimizationFlags.CoverageBasedTest)) ? null : CoverageMutants.GetTests<TestCase>(mutationId.Value);
            if (testCases == null)
            {
                Logger.LogDebug($"Testing {mutationId} against all tests.");
            }
            else
            {
                Logger.LogDebug($"Testing {mutationId} against:{string.Join(", ", testCases.Select(x => x.FullyQualifiedName))}.");
            }
            return RunVsTest(testCases, timeoutMs, envVars);
        }

        private void SetListOfTests(ICollection<TestCase> tests)
        {
            _discoveredTests = tests;
        }

        public ICollection<TestCase> DiscoverTests(string runSettings = null)
        {
            if (_discoveredTests == null)
            {
                using (var waitHandle = new AutoResetEvent(false))
                {
                    var handler = new DiscoveryEventHandler(waitHandle, _messages);
                    var generateRunSettings = GenerateRunSettings(null, false);
                    _vsTestConsole.DiscoverTests(_sources, runSettings ?? generateRunSettings, handler);

                    waitHandle.WaitOne();
                    if (handler.Aborted)
                    {
                        Logger.LogError("Test discovery has been aborted!");
                    }

                    SetListOfTests(handler.DiscoveredTestCases);
                }
            }

            return _discoveredTests;
        }

        private TestRunResult RunVsTest(ICollection<TestCase> testCases, int? timeoutMs,
            Dictionary<string, string> envVars)
        {
            var testResults = RunAllTests(testCases, envVars, GenerateRunSettings(timeoutMs, false), false);

            // For now we need to throw an OperationCanceledException when a testrun has timed out. 
            // We know the testrun has timed out because we received less test results from the test run than there are test cases in the unit test project.
            var resultAsArray = testResults as TestResult[] ?? testResults.ToArray();
            if (resultAsArray.All(x => x.Outcome != TestOutcome.Failed) && resultAsArray.Count() < (testCases ?? _discoveredTests).Count)
            {
                throw new OperationCanceledException();
            }

            var testResult = new TestRunResult
            {
                Success = resultAsArray.All(tr => tr.Outcome == TestOutcome.Passed || tr.Outcome == TestOutcome.Skipped),
                ResultMessage = string.Join(
                    Environment.NewLine,
                    resultAsArray.Where(tr => !string.IsNullOrWhiteSpace(tr.ErrorMessage))
                        .Select(tr => tr.ErrorMessage)),
                TotalNumberOfTests = _discoveredTests.Count()
            };

            return testResult;
        }

        public TestRunResult CaptureCoverage()
        {
            Logger.LogDebug($"Capturing coverage.");
            var testResults = RunAllTests(null, _coverageEnvironment, GenerateRunSettings( null, true), true);
            foreach (var testResult in testResults)
            {
                var propertyPair = testResult.GetProperties().FirstOrDefault(x => x.Key.Id == "Stryker.Coverage");
                if (propertyPair.Value != null)
                {
                    var propertyPairValue = (propertyPair.Value as string);
                    if (!string.IsNullOrWhiteSpace(propertyPairValue))
                    {
                        var coverage = propertyPairValue.Split(',').Select(int.Parse);
                        // we need to refer to the initial testCase instance, otherwise xUnit raises internal errors
                        CoverageMutants.DeclareMappingForATest(_discoveredTests.First(testCase => testCase.Id == testResult.TestCase.Id), coverage);
                    }
                }
            }
            CoveredMutants = CoverageMutants.CoveredMutants;
            return new TestRunResult { Success = true, TotalNumberOfTests = _discoveredTests.Count };
        }

        public IEnumerable<TestResult> CoverageForTest(TestCase test)
        {
            Logger.LogDebug($"Capturing coverage for {test.FullyQualifiedName}.");
            var generateRunSettings = GenerateRunSettings( 0, true);
            var testResults = RunAllTests(new List<TestCase>{test}, _coverageEnvironment, generateRunSettings, true);
            var coverageForTest = testResults as TestResult[] ?? testResults.ToArray();
            foreach (var testResult in coverageForTest)
            {
                foreach (var testResultMessage in testResult.Messages)
                {
                    Logger.LogDebug($"Test output:{Environment.NewLine}{testResultMessage.Text}");
                }

                var propertyPair = testResult.GetProperties().FirstOrDefault(x => x.Key.Id == "Stryker.Coverage");
                if (propertyPair.Value != null)
                {
                    var coverage = (propertyPair.Value as string)?.Split(',').Select(int.Parse);
                    CoverageMutants.DeclareMappingForATest(testResult.TestCase, coverage);
                    return coverageForTest;
                }

                Logger.LogWarning(
                    $"No coverage for {test.FullyQualifiedName}, maybe this test does not actually test anything.");
            }

            return coverageForTest;
        }

        private void Handler_TestsFailed(object sender, EventArgs e)
        {
            // one test has failed, we can stop
            Logger.LogDebug("At least one test failed, abort current test run.");
            _vsTestConsole.AbortTestRun();
        }

        private IEnumerable<TestResult> RunAllTests(ICollection<TestCase> testCases, Dictionary<string, string> envVars,
            string runSettings, bool forCoverage) 
        {
            using (var runCompleteSignal = new AutoResetEvent(false))
            {
                    var handler = new RunEventHandler(runCompleteSignal, _messages);
                    var testHostLauncher = new StrykerVsTestHostLauncher(null, envVars);
                    if (_flags.HasFlag(OptimizationFlags.AbortTestOnKill) && !forCoverage)
                    {
                        handler.TestsFailed += Handler_TestsFailed;
                    }

                    if (testCases != null)
                    {
                        _vsTestConsole.RunTestsWithCustomTestHost(testCases, runSettings, handler, testHostLauncher);
                    }
                    else
                    {
                        _vsTestConsole.RunTestsWithCustomTestHost(_sources, runSettings, handler, testHostLauncher);
                    }

                    // Test host exited signal comes after the run complete
                    testHostLauncher.WaitProcessExit();

                    // At this point, run must have complete. Check signal for true
                    runCompleteSignal.WaitOne();

                    handler.TestsFailed -= Handler_TestsFailed;

                    var results = handler.TestResults;
                    return results;
            }
        }

        private TraceLevel DetermineTraceLevel()
        {
            var traceLevel = TraceLevel.Off;
            switch (_options.LogOptions.LogLevel)
            {
                case LogEventLevel.Debug:
                case LogEventLevel.Verbose:
                    traceLevel = TraceLevel.Verbose;
                    break;
                case LogEventLevel.Error:
                case LogEventLevel.Fatal:
                    break;
                case LogEventLevel.Warning:
                    traceLevel = TraceLevel.Warning;
                    break;
                case LogEventLevel.Information:
                    traceLevel = TraceLevel.Info;
                    break;
            }

            Logger.LogDebug("VsTest logging set to {0}", traceLevel.ToString());
            return traceLevel;
        }

        private string GenerateRunSettings(int? timeout, bool forCoverage)
        {
            var targetFramework = _projectInfo.TestProjectAnalyzerResult.TargetFramework;

            var targetFrameworkVersion = Regex.Replace(targetFramework, @"[^.\d]", "");
            switch (targetFramework)
            {
                case string s when s.Contains("netcoreapp"):
                    targetFrameworkVersion = $".NETCoreApp,Version = v{targetFrameworkVersion}";
                    break;
                case string s when s.Contains("netstandard"):
                    throw new ApplicationException("Unsupported targetframework detected. A unit test project cannot be netstandard!: " + targetFramework);
                default:
                    targetFrameworkVersion = $"Framework40";
                    break;
            }

            var needCoverage = forCoverage && NeedCoverage();
            var dataCollectorSettings = needCoverage ? CoverageCollector.GetVsTestSettings() : "";
            var sequentialMode = needCoverage ? "<CollectDataForEachTestSeparately>true</CollectDataForEachTestSeparately>" : "";
            var timeOutSettings = timeout.HasValue ? $"<TestSessionTimeout>{timeout}</TestSessionTimeout>":"";
            var runSettings = 
                $@"<RunSettings>
 <RunConfiguration>
  <MaxCpuCount>{_options.ConcurrentTestrunners}</MaxCpuCount>
  <TargetFrameworkVersion>{targetFrameworkVersion}</TargetFrameworkVersion>
  {timeOutSettings}{sequentialMode}
 </RunConfiguration>{dataCollectorSettings}
</RunSettings>";

            Logger.LogDebug("VsTest runsettings set to: {0}", runSettings);

            return runSettings;
        }

        private bool NeedCoverage()
        {
            return _flags.HasFlag(OptimizationFlags.CoverageBasedTest) || _flags.HasFlag(OptimizationFlags.SkipUncoveredMutants);
        }

        private IVsTestConsoleWrapper PrepareVsTestConsole()
        {
            var vstestLogPath = Path.Combine(_options.OutputPath, "logs", "vstest-log.txt");
            _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(vstestLogPath));

            Logger.LogDebug("Logging vstest output to: {0}", vstestLogPath);

            return new VsTestConsoleWrapper(_vsTestHelper.GetCurrentPlatformVsTestToolPath(), new ConsoleParameters
            {
                TraceLevel = DetermineTraceLevel(),
                LogFilePath = vstestLogPath
            });
        }

        private void InitializeVsTestConsole()
        {
            var testBinariesPath = _projectInfo.GetTestBinariesPath();
            if (!_fileSystem.File.Exists(testBinariesPath))
            {
                throw new ApplicationException($"The test project binaries could not be found at {testBinariesPath}, exiting...");
            }

            var testBinariesLocation = Path.GetDirectoryName(testBinariesPath);
            _sources = new List<string>()
            {
                FilePathUtils.ConvertPathSeparators(testBinariesPath)
            };
            try
            {
                _vsTestConsole.StartSession();
                _vsTestConsole.InitializeExtensions(new List<string>
                {
                    testBinariesLocation,
                    _vsTestHelper.GetDefaultVsTestExtensionsPath(_vsTestHelper.GetCurrentPlatformVsTestToolPath())
                });
            }
            catch (Exception e)
            {
                throw new ApplicationException("Stryker failed to connect to vstest.console", e);
            }

            DiscoverTests();
        }

        #region IDisposable Support

        private void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _vsTestConsole.EndSession();
                    _vsTestHelper.Cleanup();
                }

                _disposedValue = true;
            }
        }

        ~VsTestRunner()
        {
            Dispose(true);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
