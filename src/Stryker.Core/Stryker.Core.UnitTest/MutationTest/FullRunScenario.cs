using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using Stryker.Core.Initialisation;
using Stryker.Core.Mutants;
using Stryker.Core.TestRunners;

namespace Stryker.Core.UnitTest.MutationTest
{
    /// <summary>
    /// This class simplifies the creation of run scenarios
    /// </summary>
    internal class FullRunScenario
    {
        private Dictionary<int, Mutant> _mutants = new();
        private Dictionary<int, TestDescription> _tests = new ();

        private Dictionary<int, TestsGuidList> _coverageResult = new();
        private Dictionary<int, TestsGuidList> _failedTestsPerRun = new();
        private const int InitialRunID = -1;

        public TestSet TestSet { get; } = new();
        public IDictionary<int, Mutant> Mutants => _mutants;

        /// <summary>
        /// Crearte a mutant
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public Mutant CreateMutant(int id=-1)
        {
            if (id == -1)
            {
                id = _mutants.Keys.Append(-1).Max()+1;
            }
            var mutant = new Mutant() {Id = id};
            _mutants[id] = mutant;
            return mutant;
        }

        public void CreateMutants(params int[] ids)
        {
            foreach (var id in ids)
            {
                CreateMutant(id);
            }
        }

        public IEnumerable<Mutant> GetMutants()
        {
            return _mutants.Values;
        }

        public IEnumerable<Mutant> GetCoveredMutants()
        {
            return _coverageResult.Keys.Select(i => _mutants[i]);
        }

        public MutantStatus GetMutantStatus(int id)
        {
            return _mutants[id].ResultStatus;
        }

        public void DeclareCoverageForMutant(int mutantId, params int[] testIds)
        {
            _coverageResult[mutantId] = GetGuidList(testIds);
        }

        public void DeclareTestsFailingAtInit(params int[] ids)
        {
            DeclareTestsFailingWhenTestingMutant(InitialRunID, ids);
        }

        public void DeclareTestsFailingWhenTestingMutant(int id, params int[] ids)
        {
            var testsGuidList = GetGuidList(ids);
            if (!testsGuidList.IsIncluded(GetCoveringTests(id)))
            {
                // just check we are not doing something stupid
                throw new ApplicationException(
                    $"you tried to declare a failing test but it does not cover mutant {id}");
            }
            _failedTestsPerRun[id] = testsGuidList;
        }

        /// <summary>
        /// Create a test
        /// </summary>
        /// <param name="id"></param>
        /// <param name="name"></param>
        /// <param name="file"></param>
        /// <returns></returns>
        public TestDescription CreateTest(int id = -1, string name = null, string file = "TestFile.cs")
        {
            if (id == -1)
            {
                id = _tests.Keys.Append(-1).Max()+1;
            }

            var test = new TestDescription(Guid.NewGuid(), name ?? $"test {id}", file);
            _tests[id] = test;
            TestSet.RegisterTests(new []{test});
            return test;
        }

        public void CreateTests(params int[] ids)
        {
            foreach (var id in ids)
            {
                CreateTest(id);
            }
        }

        /// <summary>
        /// Returns a list of test Guids
        /// </summary>
        /// <param name="ids"></param>
        /// <returns></returns>
        public TestsGuidList GetGuidList(params int[] ids)
        {
            return new ((ids.Length>0 ? ids.Select(i => _tests[i]) : _tests.Values).Select(t => t.Id));
        }

        private TestsGuidList GetFailedTests(int runId)
        {
            if (_failedTestsPerRun.TryGetValue(runId, out var list))
            {
                return list;
            }
            return TestsGuidList.NoTest();
        }

        private TestsGuidList GetCoveringTests(int id)
        {
            if (_coverageResult.TryGetValue(id, out var list))
            {
                return list;
            }

            if (id == InitialRunID)
            {
                return new TestsGuidList(_tests.Values.Select(t => t.Id));
            }
            return TestsGuidList.NoTest();
        }

        private TestRunResult GetRunResult(int id)
        {
            return new(TestsGuidList.EveryTest(), GetFailedTests(id), TestsGuidList.NoTest(), string.Empty, TimeSpan.Zero);
        }

        public Mock<ITestRunner> GetTestRunnerMock()
        {
            var runnerMock = new Mock<ITestRunner>();
            var successResult = new TestRunResult(GetGuidList(),
                TestsGuidList.NoTest(),
                TestsGuidList.NoTest(),
                String.Empty,
                TimeSpan.Zero);
            runnerMock.Setup(x => x.DiscoverTests()).Returns(TestSet);
            runnerMock.Setup(x => x.InitialTest()).Returns(GetRunResult(InitialRunID));
            runnerMock.Setup(x => x.CaptureCoverage(It.IsAny<IEnumerable<Mutant>>()))
                .Callback((Action<IEnumerable<Mutant>>)(t =>
                {
                    foreach (var m in t)
                    {
                        m.CoveringTests = GetCoveringTests(m.Id);
                    }
                })).Returns(successResult);
            runnerMock.Setup(x => x.TestMultipleMutants(It.IsAny<ITimeoutValueCalculator>(),
                    It.IsAny<IReadOnlyList<Mutant>>(), It.IsAny<TestUpdateHandler>())).
                Callback((Action<ITimeoutValueCalculator, IReadOnlyList<Mutant>, TestUpdateHandler>)((test1, list,
                    update) =>
                {
                    foreach (var m in list)
                    {
                        update(list, GetFailedTests(m.Id), GetCoveringTests(m.Id), TestsGuidList.NoTest());
                    }
                }))
                .Returns(successResult);
            return runnerMock;
        }
    }
}