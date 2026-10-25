namespace TowerGrid.Core;

/// <summary>格子坐标（整数格）。X 向右，Y 向下（屏幕坐标系）。</summary>
public readonly record struct CellPos(int X, int Y)
{
    public static CellPos operator +(CellPos a, CellPos b) => new(a.X + b.X, a.Y + b.Y);

    public double DistanceTo(CellPos o)
    {
        var dx = X - o.X;
        var dy = Y - o.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"({X},{Y})";
}

/// <summary>防御塔朝向：上下左右四向。技能/范围的"前方"永远指朝向一侧。</summary>
public enum Direction
{
    Up,
    Down,
    Left,
    Right,
}

public static class DirectionEx
{
    public static readonly Direction[] All = [Direction.Up, Direction.Down, Direction.Left, Direction.Right];

    /// <summary>朝向对应的单位格偏移。</summary>
    public static CellPos ToOffset(this Direction d) => d switch
    {
        Direction.Up => new CellPos(0, -1),
        Direction.Down => new CellPos(0, 1),
        Direction.Left => new CellPos(-1, 0),
        _ => new CellPos(1, 0),
    };

    /// <summary>顺时针旋转 90°（屏幕坐标系）。</summary>
    public static Direction RotateCW(this Direction d) => d switch
    {
        Direction.Up => Direction.Right,
        Direction.Right => Direction.Down,
        Direction.Down => Direction.Left,
        _ => Direction.Up,
    };

    public static Direction Opposite(this Direction d) => d switch
    {
        Direction.Up => Direction.Down,
        Direction.Down => Direction.Up,
        Direction.Left => Direction.Right,
        _ => Direction.Left,
    };
}

/// <summary>
/// 规整的方形网格地图：整张地图由 Width×Height 个正方形格子组成。
/// 敌人沿一条由相邻格子串成的路径行进（首个路点为出生点，末尾为终点）；
/// 演示为单路径，扩展多路径时把 Waypoints 换成列表即可。
/// </summary>
public sealed class GridMap
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>路径途经的关键格点（相邻路点之间按轴对齐逐格展开）。</summary>
    public required IReadOnlyList<CellPos> Waypoints { get; init; }

    // 对象初始化器先跑构造函数、后赋值属性，因此路径在首次访问时才展开
    private IReadOnlyList<CellPos>? _pathCells;
    private IReadOnlySet<CellPos>? _pathSet;

    /// <summary>路径展开后的逐格序列（敌人按格推进、判定阻挡都用它）。</summary>
    public IReadOnlyList<CellPos> PathCells => _pathCells ??= ExpandWaypoints();

    public IReadOnlySet<CellPos> PathSet => _pathSet ??= new HashSet<CellPos>(PathCells);

    /// <summary>路径总长（格）。敌人的 PathDist ∈ [0, PathLength]。</summary>
    public double PathLength => PathCells.Count - 1;

    public CellPos SpawnCell => PathCells[0];
    public CellPos ExitCell => PathCells[^1];

    private List<CellPos> ExpandWaypoints()
    {
        var cells = new List<CellPos>();
        for (var i = 0; i < Waypoints.Count; i++)
        {
            var cur = Waypoints[i];
            if (i == 0)
            {
                cells.Add(cur);
                continue;
            }
            var prev = Waypoints[i - 1];
            var dx = Math.Sign(cur.X - prev.X);
            var dy = Math.Sign(cur.Y - prev.Y);
            var p = prev;
            while (p != cur)
            {
                p = new CellPos(p.X + (p.X != cur.X ? dx : 0), p.Y + (p.X == cur.X ? dy : 0));
                cells.Add(p);
            }
        }
        return cells;
    }

    public bool InBounds(CellPos p) => p.X >= 0 && p.X < Width && p.Y >= 0 && p.Y < Height;

    public bool IsOnPath(CellPos p) => PathSet.Contains(p);

    /// <summary>路径距离所在的格（四舍五入），供索敌/阻挡判定。</summary>
    public CellPos CellAt(double dist) => PathCells[(int)Math.Round(Math.Clamp(dist, 0, PathLength))];

    /// <summary>路径距离处的浮点位置（格间插值），供渲染与特效。</summary>
    public (double X, double Y) PosAt(double dist)
    {
        var d = Math.Clamp(dist, 0, PathLength);
        var i = (int)Math.Floor(d);
        var t = d - i;
        var a = PathCells[i];
        var b = PathCells[Math.Min(PathCells.Count - 1, i + 1)];
        return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }
}
