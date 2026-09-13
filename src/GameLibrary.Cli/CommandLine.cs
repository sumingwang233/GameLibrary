namespace GameLibrary.Cli;

/// <summary>
/// 最小命令行解析：名词 动词 + 全局 flag（--data-dir/--format/--timeout/--operation/--no-start）。
/// 不引入交互；未知参数直接参数错误（退出码 2）。
/// </summary>
internal sealed record CommandLine
{
    private CommandLine(bool isValid, string? error)
    {
        IsValid = isValid;
        Error = error;
    }

    public bool IsValid { get; }

    public string? Error { get; }

    public IReadOnlyList<string> Words { get; private init; } = [];

    public string? DataDir { get; private init; }

    public string? OperationId { get; private init; }

    /// <summary>schema get 的 --operation 参数（操作 ID）。</summary>
    public string? OperationArgument { get; private init; }

    /// <summary>scan start 的 --root 参数或 scan inspect 的 --path 参数。</summary>
    public string? RootArgument { get; private init; }

    /// <summary>作业查询/取消的 --job-id 参数。</summary>
    public string? JobId { get; private init; }

    /// <summary>candidates get 的 --candidate-id 参数。</summary>
    public string? CandidateId { get; private init; }

    /// <summary>profiles / launch 的 --profile-id 参数。</summary>
    public string? ProfileId { get; private init; }

    /// <summary>launch execute 的 --plan-id 参数。</summary>
    public string? PlanId { get; private init; }

    /// <summary>profiles / launch 的 --game-id 参数。</summary>
    public string? GameId { get; private init; }

    /// <summary>profiles create 的 --exe 参数（绝对路径）。</summary>
    public string? ExePath { get; private init; }

    /// <summary>profiles create 的 --cwd 参数（绝对路径）。</summary>
    public string? Cwd { get; private init; }

    /// <summary>profiles create 的 --arg 参数（可重复，构成 argv）。</summary>
    public IReadOnlyList<string> ArgList { get; private init; } = [];

    /// <summary>launch execute 的 --idempotency-key 参数。</summary>
    public string? IdempotencyKey { get; private init; }

    /// <summary>launch execute 的 --expected-revision 参数。</summary>
    public int? ExpectedRevision { get; private init; }

    /// <summary>launch status 的 --attempt-id 参数。</summary>
    public string? AttemptId { get; private init; }

    public bool NoStart { get; private init; }

    public int TimeoutSeconds { get; private init; } = 30;

    public string RequestId { get; } = $"cli-{Guid.NewGuid():N}";

    public static CommandLine Parse(string[] args)
    {
        if (args.Length < 2)
        {
            return Invalid("用法：gamelibrary <名词> <动词> [参数]，例如 gamelibrary schema get --operation games.update");
        }

        var noun = args[0].ToLowerInvariant();
        var verb = args[1].ToLowerInvariant();
        string? dataDir = null;
        string? operationArg = null;
        string? rootArg = null;
        string? jobId = null;
        string? candidateId = null;
        string? profileId = null;
        string? planId = null;
        string? gameId = null;
        string? exePath = null;
        string? cwd = null;
        string? idempotencyKey = null;
        string? attemptId = null;
        int? expectedRevision = null;
        var argList = new List<string>();
        var noStart = false;
        var timeout = 30;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--operation" when i + 1 < args.Length:
                    operationArg = args[++i];
                    break;
                case "--root" or "--path" when i + 1 < args.Length:
                    rootArg = args[++i];
                    break;
                case "--job-id" when i + 1 < args.Length:
                    jobId = args[++i];
                    break;
                case "--candidate-id" when i + 1 < args.Length:
                    candidateId = args[++i];
                    break;
                case "--profile-id" when i + 1 < args.Length:
                    profileId = args[++i];
                    break;
                case "--plan-id" when i + 1 < args.Length:
                    planId = args[++i];
                    break;
                case "--game-id" when i + 1 < args.Length:
                    gameId = args[++i];
                    break;
                case "--exe" when i + 1 < args.Length:
                    exePath = args[++i];
                    break;
                case "--cwd" when i + 1 < args.Length:
                    cwd = args[++i];
                    break;
                case "--arg" when i + 1 < args.Length:
                    argList.Add(args[++i]);
                    break;
                case "--idempotency-key" when i + 1 < args.Length:
                    idempotencyKey = args[++i];
                    break;
                case "--expected-revision" when i + 1 < args.Length && int.TryParse(args[i + 1], out var revision):
                    expectedRevision = revision;
                    i++;
                    break;
                case "--attempt-id" when i + 1 < args.Length:
                    attemptId = args[++i];
                    break;
                case "--no-start":
                    noStart = true;
                    break;
                case "--format" when i + 1 < args.Length && args[i + 1].Equals("json", StringComparison.OrdinalIgnoreCase):
                    i++;
                    break;
                case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds) && seconds > 0:
                    timeout = seconds;
                    i++;
                    break;
                default:
                    return Invalid($"未知或不完整的参数：{args[i]}");
            }
        }

        var operationId = noun switch
        {
            "capabilities" when verb == "get" => "capabilities.get",
            "schema" when verb == "get" => "schema.get",
            "host" when verb is "status" or "start" or "stop" => $"host.{verb}",
            "scan" when verb is "start" or "status" or "cancel" or "coverage" or "inspect" => $"scan.{verb}",
            "candidates" when verb is "list" or "get" => $"candidates.{verb}",
            "profiles" when verb is "create" or "list" or "get" or "update" => $"profiles.{verb}",
            "launch" when verb is "plan" or "execute" or "status" or "history" => $"launch.{verb}",
            "jobs" when verb is "get" or "list" or "wait" or "cancel" => verb == "get" ? "jobs.get" : null,
            _ => null,
        };

        if (operationId is null)
        {
            return Invalid($"未知命令：{noun} {verb}");
        }

        return new CommandLine(true, null)
        {
            Words = [noun, verb],
            DataDir = dataDir,
            OperationId = operationId,
            OperationArgument = operationArg,
            RootArgument = rootArg,
            JobId = jobId,
            CandidateId = candidateId,
            ProfileId = profileId,
            PlanId = planId,
            GameId = gameId,
            ExePath = exePath,
            Cwd = cwd,
            ArgList = argList,
            IdempotencyKey = idempotencyKey,
            ExpectedRevision = expectedRevision,
            AttemptId = attemptId,
            NoStart = noStart,
            TimeoutSeconds = timeout,
        };
    }

    private static CommandLine Invalid(string message) => new(false, message);
}
