using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DefaultEcs.Threading;

public class CustomRunnable : IParallelRunnable
{
    private readonly int _count;
    public CustomRunnable(int count = 10)
    {
        _count = count;
    }
    public void Run(int index, int maxIndex)
    {
        var size = (float)_count / (maxIndex + 1);
        var thisSize = (int)Math.Round((size + 1) * index);

        Thread.Sleep(thisSize * 10);
    }
}

public class TaskRunner : IParallelRunner
{
    public TaskRunner(int degreeOfParallelism = 2)
    {
        DegreeOfParallelism = degreeOfParallelism;
    }

    public int DegreeOfParallelism { get; }

    public void Run(IParallelRunnable runnable)
    {
        Enumerable.Range(0, DegreeOfParallelism).AsParallel().ForAll(i => runnable.Run(i, DegreeOfParallelism));
    }

    public void Dispose() { }
}

public class BarrierRunner : IParallelRunner
{
    private readonly Task[] _tasks;
    private readonly CancellationTokenSource _disposeToken;
    private readonly ManualResetEventSlim _signal;
    private readonly ManualResetEventSlim _finished;
    private readonly Barrier _barrier;

    private IParallelRunnable _currentRunnable;

    public BarrierRunner(int degreeOfParallelism = 2)
    {
        _tasks = Enumerable.Range(0, degreeOfParallelism).Select(i => new Task(Update, i, TaskCreationOptions.LongRunning)).ToArray();
        _disposeToken = new CancellationTokenSource();
        _signal = new ManualResetEventSlim(false);
        _finished = new ManualResetEventSlim(false);
        _barrier = new Barrier(degreeOfParallelism, _ => { _currentRunnable = null; _signal.Reset(); _finished.Set(); });

        for (var i = 0; i < _tasks.Length; i++)
        {
            _tasks[i].Start(TaskScheduler.Default);
        }
    }

    public int DegreeOfParallelism => _tasks.Length;

    public void Run(IParallelRunnable runnable)
    {
        lock (_finished)
        {
            _finished.Reset();
            _currentRunnable = runnable;
            _signal.Set();
            _finished.Wait();
        }
    }

    public void Dispose()
    {
        _disposeToken.Cancel();
        Task.WaitAll(_tasks);
        _disposeToken.Dispose();
    }

    private void Update(object state)
    {
        var i = (int)state;
        _signal.Wait();
        while (!_disposeToken.IsCancellationRequested)
        {
            _currentRunnable.Run(i, DegreeOfParallelism);
            _barrier.SignalAndWait();
            _signal.Wait();
        }
    }
}

[MemoryDiagnoser]
[AsciiDocExporter]
[PlainExporter]
[MarkdownExporterAttribute.GitHub]
public class ParallelRunnerBenchmarks
{
    private IParallelRunnable _runnable;
    private DefaultParallelRunner _defaultParallelRunner;
    private TaskRunner _taskRunner;
    private BarrierRunner _barrierRunner;

    [Params(1, 5, 10)]
    public int TaskCount { get; set; }

    [Params(1, 2, 10)]
    public int DegreeOfParallelism { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _runnable = new CustomRunnable(TaskCount);
        _defaultParallelRunner = new DefaultParallelRunner(DegreeOfParallelism);
        _taskRunner = new TaskRunner(DegreeOfParallelism);
        _barrierRunner = new BarrierRunner(DegreeOfParallelism);
    }

    [Benchmark(Baseline = true)]
    public void DefaultParallelRunner() => _defaultParallelRunner.Run(_runnable);

    [Benchmark]
    public void TaskRunner() => _taskRunner.Run(_runnable);

    [Benchmark]
    public void BarrierRunner() => _barrierRunner.Run(_runnable);
}

public static class Program
{
    public static void Main(string[] args)
    {
        //BenchmarkRunner.Run(typeof(Program).Assembly, new DebugInProcessConfig());
        BenchmarkRunner.Run(typeof(Program).Assembly);
    }
}
