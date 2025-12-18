using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public static class RwLockVsLockBenchmarkInteractive
{
    // -------------------------
    // 고정 설정 (필요하면 여기만 수정)
    // -------------------------
    private const int Threads = 8;
    private const int DurationSeconds = 3;
    private const int WarmupSeconds = 1;

    // "읽기/쓰기 구간" 가짜 작업량 (상대 차이를 보기 위해 유지)
    private const int ReadWork = 50;
    private const int WriteWork = 20;

    // 공유 상태
    private static int _value;

    // lock용
    private static readonly object _gate = new object();

    // RWLock용
    private static readonly ReaderWriterLockSlim _rw =
        new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

    public static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($".NET: {Environment.Version}");
        Console.WriteLine($"CPU Cores: {Environment.ProcessorCount}");
        Console.WriteLine($"Threads={Threads}, Duration={DurationSeconds}s, ReadWork={ReadWork}, WriteWork={WriteWork}");
        Console.WriteLine(new string('-', 96));

        int caseCount = ReadInt("테스트 케이스 개수 입력: ", min: 1, max: 50);

        var readPercents = new List<int>(caseCount);
        for (int i = 0; i < caseCount; i++)
        {
            int rp = ReadInt($"케이스 {i + 1}의 Read% 입력 (0~100): ", min: 0, max: 100);
            readPercents.Add(rp);
        }

        Console.WriteLine("\n워밍업 중...");
        Warmup(() => RunLockTest(WarmupSeconds, readPercent: 100));
        Warmup(() => RunRwLockTest(WarmupSeconds, readPercent: 100));

        Console.WriteLine("워밍업 완료.");
        Console.WriteLine(new string('-', 96));

        var results = new List<CaseResult>(caseCount);

        for (int i = 0; i < caseCount; i++)
        {
            int readPercent = readPercents[i];
            Console.WriteLine($"\n[케이스 {i + 1}/{caseCount}] Read%={readPercent} 벤치 실행...");

            // lock 측정
            var lockBench = RunBench(
                name: "lock(Monitor)",
                run: () => RunLockTest(DurationSeconds, readPercent));

            // RWLockSlim 측정
            var rwBench = RunBench(
                name: "ReaderWriterLockSlim",
                run: () => RunRwLockTest(DurationSeconds, readPercent));

            results.Add(CaseResult.From(readPercent, lockBench, rwBench));
        }

        // 한눈에 보는 요약표 출력
        Console.WriteLine("\n" + new string('=', 110));
        Console.WriteLine("요약 (처리량 ops/s 기준, RWLockSlim이 lock 대비 얼마나 개선되었는지)");
        Console.WriteLine(new string('=', 110));
        PrintSummaryTable(results);

        Console.WriteLine("\nTip) ReadWork를 크게(예: 500~2000) 하면 Read-heavy에서 RWLockSlim 우세가 더 뚜렷해질 수 있어요.");
    }

    // -------------------------
    // 공통: 벤치 실행 + 출력
    // -------------------------
    private static BenchResult RunBench(string name, Func<RunResult> run)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var sw = Stopwatch.StartNew();
        var r = run();
        sw.Stop();

        double secs = sw.Elapsed.TotalSeconds;
        double opsPerSec = r.TotalOps / secs;

        Console.WriteLine($"{name,-22} | ops={r.TotalOps,12:N0} | ops/s={opsPerSec,12:N0} | reads={r.ReadOps,12:N0} | writes={r.WriteOps,10:N0} | elapsed={sw.ElapsedMilliseconds,6} ms");
        return new BenchResult(name, r, sw.Elapsed, opsPerSec);
    }

    // -------------------------
    // lock(Monitor) 테스트
    // -------------------------
    private static RunResult RunLockTest(int seconds, int readPercent)
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
                bool alwaysRead = readPercent >= 100;
                bool alwaysWrite = readPercent <= 0;

                var rnd = (alwaysRead || alwaysWrite) ? null : new Random(seed);

                long localReads = 0, localWrites = 0;

                while (!cts.IsCancellationRequested)
                {
                    bool isRead;
                    if (alwaysRead) isRead = true;
                    else if (alwaysWrite) isRead = false;
                    else isRead = rnd!.Next(100) < readPercent;

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
        return new RunResult(readOps, writeOps);
    }

    // -------------------------
    // ReaderWriterLockSlim 테스트
    // -------------------------
    private static RunResult RunRwLockTest(int seconds, int readPercent)
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
                bool alwaysRead = readPercent >= 100;
                bool alwaysWrite = readPercent <= 0;

                var rnd = (alwaysRead || alwaysWrite) ? null : new Random(seed);

                long localReads = 0, localWrites = 0;

                while (!cts.IsCancellationRequested)
                {
                    bool isRead;
                    if (alwaysRead) isRead = true;
                    else if (alwaysWrite) isRead = false;
                    else isRead = rnd!.Next(100) < readPercent;

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
        return new RunResult(readOps, writeOps);
    }

    // -------------------------
    // 가짜 작업 (최적화 방지 + read 길이 조절)
    // -------------------------
    private static void BusyWork(int iters, int seed)
    {
        int x = seed;
        for (int i = 0; i < iters; i++)
            x = (x * 1103515245 + 12345) ^ (x >> 16);

        // 아주 희박한 방식으로 소비해서 JIT이 완전 제거 못 하게
        if ((x & 0xFFFF) == 0x1234) Thread.SpinWait(1);
    }

    // -------------------------
    // 워밍업/입력/출력
    // -------------------------
    private static void Warmup(Action action)
    {
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static int ReadInt(string prompt, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);
            string? s = Console.ReadLine();

            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v >= min && v <= max)
                return v;

            Console.WriteLine($"  ⚠️  {min}~{max} 범위의 정수를 입력해 주세요.");
        }
    }

    private static void PrintSummaryTable(List<CaseResult> results)
    {
        // 헤더
        Console.WriteLine(
            $"{"Read%",5} | {"lock ops/s",12} | {"RW ops/s",12} | {"배수(RW/lock)",12} | {"개선(%)",9} | {"Winner",14}");

        Console.WriteLine(new string('-', 110));

        foreach (var r in results)
        {
            string winner = r.RwOpsPerSec >= r.LockOpsPerSec ? "RWLockSlim" : "lock";
            Console.WriteLine(
                $"{r.ReadPercent,5} | {r.LockOpsPerSec,12:N0} | {r.RwOpsPerSec,12:N0} | {r.Ratio,12:F2} | {r.ImprovementPercent,9:F1} | {winner,14}");
        }
    }

    // -------------------------
    // 결과 타입들
    // -------------------------
    private readonly record struct RunResult(long ReadOps, long WriteOps)
    {
        public long TotalOps => ReadOps + WriteOps;
    }

    private readonly record struct BenchResult(string Name, RunResult Run, TimeSpan Elapsed, double OpsPerSec);

    private readonly record struct CaseResult(int ReadPercent, double LockOpsPerSec, double RwOpsPerSec, double Ratio, double ImprovementPercent)
    {
        public static CaseResult From(int readPercent, BenchResult lockBench, BenchResult rwBench)
        {
            double ratio = rwBench.OpsPerSec / lockBench.OpsPerSec;
            double improvement = (ratio - 1.0) * 100.0;
            return new CaseResult(readPercent, lockBench.OpsPerSec, rwBench.OpsPerSec, ratio, improvement);
        }
    }
}
