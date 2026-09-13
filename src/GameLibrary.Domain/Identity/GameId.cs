namespace GameLibrary.Domain.Identity;

/// <summary>
/// 游戏安装实例的永久身份（ADR-0001）：独立随机 GUID。
/// 绝不由路径+引擎+哈希派生；移动/重命名/升级不改变 GameId。
/// </summary>
public readonly record struct GameId(Guid Value)
{
    public static GameId NewGameId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");

    public static GameId Parse(string text) => new(Guid.Parse(text));

    public static bool TryParse(string? text, out GameId gameId)
    {
        if (Guid.TryParse(text, out var guid))
        {
            gameId = new GameId(guid);
            return true;
        }

        gameId = default;
        return false;
    }
}
