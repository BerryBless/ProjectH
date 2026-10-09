using System.Numerics;
using ProjectH.Shared.Simulation;

namespace ProjectH.QA;

// Stress (D38, request §22 "PlayerIndex Offset으로 영역 분리"): the run's build sites (StressMap.BuildSites over the whole
// map, every piece type) and how many builders hold each. A builder holds one site at a time: its first is claimed when
// the group is set up (runner flow, in member order: deterministic), the next ones when it finishes or gives up a site
// (pump thread). A claim takes the least-held site nearest to the given point, so two builders never work on the same
// cells while free sites remain; once every site is held, claims share the least-held ones (counted in Shared: their
// requests come back Occupied, and the report shows it). A builder that gives a site up (could not reach it) releases
// it, so the pool does not run dry by give-ups.
//
// Lock: _gate is the only lock here, held for one claim or release (a scan of at most a few hundred sites), never while
// calling out, never nested with another lock (the pump has none; the runner holds none while calling). Lifetime: one
// per run (GroupRegistry.SitePool), dropped with the run.
public sealed class BuildSitePool
{
    private readonly object _gate = new();
    private readonly StressMap.BuildSite[] _sites;
    private readonly int[] _holders;

    // 기능: 사이트 목록으로 pool을 만든다.
    // 입력: sites - run의 건설 사이트.
    // 출력: 모든 사이트의 보유자가 0인 BuildSitePool.
    public BuildSitePool(IReadOnlyList<StressMap.BuildSite> sites)
    {
        _sites = sites.ToArray();
        _holders = new int[_sites.Length];
    }

    public int Count => _sites.Length;

    // Sites nobody holds.
    public int Free
    {
        get { lock (_gate) return _holders.Count(h => h == 0); }
    }

    // Holders beyond the first, over every site (0 while each builder has a site of its own).
    public int Shared
    {
        get { lock (_gate) return _holders.Sum(h => Math.Max(0, h - 1)); }
    }

    // 기능: avoid를 뺀 사이트 중 보유자가 가장 적고 near에 가장 가까운 것을 잡아 보유자 수를 늘린다.
    // 입력: near - 기준 XZ 위치, avoid - 피할 사이트(유일한 사이트면 무시).
    // 출력: 잡은 사이트. pool이 비어 있으면 null.
    // The least-held site nearest `near` (not `avoid`, unless it is the only one). Null only when the pool is empty.
    public StressMap.BuildSite? Claim(Vector2 near, StressMap.BuildSite? avoid = null)
    {
        if (_sites.Length == 0) return null;
        lock (_gate)
        {
            int best = -1;
            int bestHolders = int.MaxValue;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _sites.Length; i++)
            {
                if (ReferenceEquals(_sites[i], avoid) && _sites.Length > 1) continue;
                float d = Vector2.DistanceSquared(near, new Vector2(_sites[i].Stand.X, _sites[i].Stand.Z));
                if (_holders[i] < bestHolders || (_holders[i] == bestHolders && d < bestDistance))
                {
                    best = i;
                    bestHolders = _holders[i];
                    bestDistance = d;
                }
            }
            _holders[best]++;
            return _sites[best];
        }
    }

    // 기능: 사이트의 보유자 수를 하나 줄인다.
    // 입력: site - 놓을 사이트.
    // 출력: 반환값 없음. pool의 사이트면 보유자 수가 1 준다(0 밑으로는 안 간다).
    // The builder no longer holds this site (it gave it up). A site the builder built on stays held.
    public void Release(StressMap.BuildSite site)
    {
        lock (_gate)
        {
            int i = Array.IndexOf(_sites, site);
            if (i >= 0 && _holders[i] > 0) _holders[i]--;
        }
    }

    // 기능: 사이트 조각 중 요청한 종류만 골라 재질을 바꾼다.
    // 입력: site - 사이트, types - 포함할 조각 종류, material - 재질.
    // 출력: 사이트 순서의 BuildPlan 배열.
    // A site's pieces of the given types, in the site's order, in the given material.
    public static BuildPlan[] PiecesOf(StressMap.BuildSite site, IReadOnlyCollection<BuildPieceType> types, BuildMaterialType material) =>
        site.Pieces.Where(p => types.Contains(p.Piece)).Select(p => p with { Material = material }).ToArray();
}
