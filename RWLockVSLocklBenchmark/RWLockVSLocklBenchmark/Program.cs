using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public static class RwLockVsLockBenchmark
{
    // -------------------------
    // 설정값
    // -------------------------
    private const int Threads = 8;
    private const int DurationSeconds = 3;
    private const int WarmupSeconds = 1;

    // Read%를 바꾸면 시나리오가 바뀝니다 (예: 50/80/95/99/100)
    private const int ReadPercent = 100;

    // 읽기/쓰기 구간에서 하는 "가짜 작업량" (짧/긴 read 조절)
    private const int ReadWork = 50;
    private const int WriteWork = 20;

    // 공유 상태
    private static int _value;

    // lock용
    private static readonly object _gate = new object();

    // RWLock용
    private static readonly ReaderWriterLockSlim _rw = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

    public static void Main()
    {
        Console.WriteLine($".NET: {Environment.Version}");
        Console.WriteLine($"CPU Cores: {Environment.ProcessorCount}");
        Console.WriteLine($"Threads={Threads}, Duration={DurationSeconds}s, Read%={ReadPercent}, ReadWork={ReadWork}, WriteWork={WriteWork}");
        Console.WriteLine(new string('-', 96));

        // 워밍업 (JIT/티어드 컴파일 영향 줄이기)
        Warmup(() => RunLockTest(WarmupSeconds));
        Warmup(() => RunRwLockTest(WarmupSeconds));

        // 본 테스트
        var lockResult = RunAndPrint("lock(Monitor)", () => RunLockTest(DurationSeconds));
        var rwResult = RunAndPrint("ReaderWriterLockSlim", () => RunRwLockTest(DurationSeconds));

        Console.WriteLine(new string('-', 96));
        PrintComparison(lockResult, rwResult);
        Console.WriteLine();

        Console.WriteLine("Tip) Read%를 50/80/95/99/100으로 바꾸거나 ReadWork를 크게 하면 경향이 더 잘 보입니다.");
    }

    private static void Warmup(Action action)
    {
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static BenchmarkResult RunAndPrint(string name, Func<Result> run)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        var result = run();
        sw.Stop();

        double secs = sw.Elapsed.TotalSeconds;
        double opsPerSec = result.TotalOps / secs;

        var bench = new BenchmarkResult(name, result, sw.Elapsed, opsPerSec);

        Console.WriteLine(
            $"{name,-22} | ops={result.TotalOps,12:N0} | ops/s={opsPerSec,12:N0} | reads={result.ReadOps,12:N0} | writes={result.WriteOps,10:N0} | elapsed={sw.ElapsedMilliseconds,6} ms");

        return bench;
    }

    private static void PrintComparison(BenchmarkResult lockBench, BenchmarkResult rwBench)
    {
        // 기준: lock 대비 RWLockSlim이 얼마나 빠른지
        double ratio = rwBench.OpsPerSec / lockBench.OpsPerSec;
        double percent = (ratio - 1.0) * 100.0;

        string winner = ratio > 1.0 ? rwBench.Name : lockBench.Name;
        string loser = ratio > 1.0 ? lockBench.Name : rwBench.Name;

        double winnerOps = Math.Max(lockBench.OpsPerSec, rwBench.OpsPerSec);
        double loserOps = Math.Min(lockBench.OpsPerSec, rwBench.OpsPerSec);
        double winRatio = winnerOps / loserOps;
        double winPct = (winRatio - 1.0) * 100.0;

        Console.WriteLine("성능 비교(처리량 기준)");
        Console.WriteLine($"- {rwBench.Name} / {lockBench.Name} = {ratio:F2}x ({(percent >= 0 ? "+" : "")}{percent:F1}%)");
        Console.WriteLine($"- Winner: {winner}  → {winRatio:F2}x faster ({winPct:F1}% higher ops/s) than {loser}");
    }

    // -------------------------
    // lock(Monitor) 테스트
    // -------------------------
    private static Result RunLockTest(int seconds)
    {
        _value = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long readOps = 0, writeOps = 0;

        Task[] tasks = new Task[Threads];
        for (int i = 0; i < Threads; i++)
        {
            int seed = Environment.TickCount ^ (i * 397);
            tasks[i] = Task.Run(() =>
            {
                // Read%가 100이면 Random 자체가 잡음이므로 제거
                bool alwaysRead = ReadPercent >= 100;
                var rnd = alwaysRead ? null : new Random(seed);

                long localReads = 0, localWrites = 0;

                while (!cts.IsCancellationRequested)
                {
                    bool isRead = alwaysRead || (rnd!.Next(100) < ReadPercent);

                    if (isRead)
                    {
                        int snapshot;
                        lock (_gate)
                        {
                            snapshot = _value;
                            BusyWork(ReadWork, snapshot);
                        }
                        localReads++;
                    }
                    else
                    {
                        lock (_gate)
                        {
                            _value++;
                            BusyWork(WriteWork, _value);
                        }
                        localWrites++;
                    }
                }

                Interlocked.Add(ref readOps, localReads);
                Interlocked.Add(ref writeOps, localWrites);
            }, cts.Token);
        }

        Task.WaitAll(tasks);
        return new Result(readOps, writeOps);
    }

    // -------------------------
    // ReaderWriterLockSlim 테스트
    // -------------------------
    private static Result RunRwLockTest(int seconds)
    {
        _value = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        long readOps = 0, writeOps = 0;

        Task[] tasks = new Task[Threads];
        for (int i = 0; i < Threads; i++)
        {
            int seed = Environment.TickCount ^ (i * 397);
            tasks[i] = Task.Run(() =>
            {
                bool alwaysRead = ReadPercent >= 100;
                var rnd = alwaysRead ? null : new Random(seed);

                long localReads = 0, localWrites = 0;

                while (!cts.IsCancellationRequested)
                {
                    bool isRead = alwaysRead || (rnd!.Next(100) < ReadPercent);

                    if (isRead)
                    {
                        _rw.EnterReadLock();
                        try
                        {
                            int snapshot = _value;
                            BusyWork(ReadWork, snapshot);
                        }
                        finally
                        {
                            _rw.ExitReadLock();
                        }
                        localReads++;
                    }
                    else
                    {
                        _rw.EnterWriteLock();
                        try
                        {
                            _value++;
                            BusyWork(WriteWork, _value);
                        }
                        finally
                        {
                            _rw.ExitWriteLock();
                        }
                        localWrites++;
                    }
                }

                Interlocked.Add(ref readOps, localReads);
                Interlocked.Add(ref writeOps, localWrites);
            }, cts.Token);
        }

        Task.WaitAll(tasks);
        return new Result(readOps, writeOps);
    }

    // -------------------------
    // 가짜 작업 (최적화로 사라지지 않게 약간의 계산)
    // -------------------------
    private static void BusyWork(int iters, int seed)
    {
        int x = seed;
        for (int i = 0; i < iters; i++)
            x = (x * 1103515245 + 12345) ^ (x >> 16);

        // JIT이 완전히 없애지 못하도록 아주 희박한 조건으로 사용
        if ((x & 0xFFFF) == 0x1234) Thread.SpinWait(1);
    }

    private readonly record struct Result(long ReadOps, long WriteOps)
    {
        public long TotalOps => ReadOps + WriteOps;
    }

    private readonly record struct BenchmarkResult(string Name, Result Result, TimeSpan Elapsed, double OpsPerSec)
    {
        public long TotalOps => Result.TotalOps;
        public long ReadOps => Result.ReadOps;
        public long WriteOps => Result.WriteOps;
    }
}
