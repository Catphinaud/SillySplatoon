using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Splatoon.SplatoonScripting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SplatoonScriptsOfficial.Generic;

public unsafe class LittleLadiesDay2026FateHelper : SplatoonScript
{
    public override Metadata Metadata { get; } = new(
        8,
        "Catphinaud",
        "Little Ladies Day 2026 helper: targets Picot/Ulala/Narumi/Masha during active FATE"
    );
    public override HashSet<uint>? ValidTerritories { get; } = [130];
    private static readonly HashSet<ushort> EventFateIds = [2042, 2043, 2044, 2045];
    private const int ActionIntervalMs = 11000;
    private const int NpcReuseWindowMs = 120000;

    // Fallback by DataId (from the original event script).
    private readonly Dictionary<uint, uint> _dataIdToActionId = new()
    {
        [18859] = 44501, // Cheer Rhythm: Red
        [18860] = 44502, // Cheer Rhythm: Yellow
        [18861] = 44503, // Cheer Rhythm: Blue
        [18862] = 44504, // Cheer Rhythm: Violet
    };

    // Name mapping requested by user layout.
    private readonly Dictionary<string, uint> _nameToActionId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ulala"] = 44501, // Red
        ["Masha Mhakaracca"] = 44502, // Yellow
        ["Narumi"] = 44503, // Blue
        ["Picot"] = 44504, // Violet
    };

    private Config C => Controller.GetConfig<Config>();
    private int _targetIndex;
    private readonly Dictionary<string, NpcUseRecord> _recentNpcUses = new();
    private static bool HasEventStatus() => Player.Status.Any(x => x.StatusId == 1494);

    private sealed class NpcUseRecord
    {
        public Vector3 Position;
        public long UsedAt;
    }
    private bool IsEventFateActive()
    {
        var fm = FateManager.Instance();
        if(fm == null) return false;
        var current = fm->GetCurrentFateId();
        return EventFateIds.Contains(current);
    }

    private string GetFateDebug()
    {
        var fm = FateManager.Instance();
        if(fm == null) return "FateManager=null";
        return $"current={fm->GetCurrentFateId()} accepted=2042/2043/2044/2045";
    }

    private static string GetNpcKey(IBattleChara npc) => $"{npc.DataId}:{npc.Name.TextValue.Trim()}";

    private void CleanupNpcUseCache(long now)
    {
        foreach(var key in _recentNpcUses.Where(x => now - x.Value.UsedAt > NpcReuseWindowMs).Select(x => x.Key).ToArray())
        {
            _recentNpcUses.Remove(key);
        }
    }

    private bool IsNpcReadyForPoints(IBattleChara npc)
    {
        var key = GetNpcKey(npc);
        if(!_recentNpcUses.TryGetValue(key, out var record)) return true;

        var elapsed = Environment.TickCount64 - record.UsedAt;
        if(elapsed > NpcReuseWindowMs) return true;

        // If NPC moved enough since last successful action, treat it as a new scoring opportunity.
        return Vector3.Distance(npc.Position, record.Position) >= C.NpcMovedThreshold;
    }

    private IEnumerable<IBattleChara> GetCandidates()
    {
        CleanupNpcUseCache(Environment.TickCount64);
        return Svc.Objects
            .OfType<IBattleChara>()
            .Where(x => x.IsTargetable && !x.IsDead && Player.DistanceTo(x) <= C.MaxDistance)
            .Where(x => _dataIdToActionId.ContainsKey(x.DataId) || _nameToActionId.ContainsKey(x.Name.TextValue))
            .Where(IsNpcReadyForPoints)
            .OrderBy(Player.DistanceTo);
    }

    private bool TryGetMappedAction(IBattleChara target, out uint actionId)
    {
        if(_dataIdToActionId.TryGetValue(target.DataId, out actionId))
            return true;
        return _nameToActionId.TryGetValue(target.Name.TextValue.Trim(), out actionId);
    }

    private static bool TryUseMappedAction(uint actionId, ulong targetId)
    {
        var am = ActionManager.Instance();
        if(am == null) return false;
        return am->UseAction(ActionType.Action, actionId, targetId);
    }

    private void TryCastOnCurrentTarget()
    {
        if(C.TargetOnlyMode) return;
        if(!EzThrottler.Check("LittleLadies2026_UseAny")) return;
        if(Svc.Targets.Target is not IBattleChara target) return;
        if(!IsNpcReadyForPoints(target)) return;
        if(!TryGetMappedAction(target, out var actionId)) return;

        if(TryUseMappedAction(actionId, target.GameObjectId))
        {
            EzThrottler.Throttle("LittleLadies2026_UseAny", ActionIntervalMs, true);
            _recentNpcUses[GetNpcKey(target)] = new()
            {
                Position = target.Position,
                UsedAt = Environment.TickCount64
            };
        }
    }

    private bool IsValidEventTarget(IGameObject? target)
    {
        if(target is not IBattleChara battleTarget) return false;
        if(!battleTarget.IsTargetable || battleTarget.IsDead) return false;
        if(Player.DistanceTo(battleTarget) > C.MaxDistance) return false;
        return TryGetMappedAction(battleTarget, out _);
    }

    public override void OnUpdate()
    {
        if(C.RequireFate2042Active && !IsEventFateActive()) return;
        if(!HasEventStatus()) return;
        if(!C.RetargetOffPlayers && Svc.Targets.Target is IPlayerCharacter) return;

        var candidates = GetCandidates().ToList();
        if(candidates.Count == 0) return;

        if(!IsValidEventTarget(Svc.Targets.Target) && EzThrottler.Throttle("LittleLadies2026_Retarget", C.RetargetMs))
        {
            _targetIndex %= candidates.Count;
            Svc.Targets.Target = candidates[_targetIndex];
            _targetIndex++;
            Controller.Schedule(TryCastOnCurrentTarget, 150);
            return;
        }

        TryCastOnCurrentTarget();
    }

    public override void OnSettingsDraw()
    {
        ImGui.Text("Active in Ul'dah (territory 130).");
        ImGui.Text($"FATE 2042 active: {IsEventFateActive()}");
        ImGui.Text($"FATE debug: {GetFateDebug()}");
        ImGui.Text($"Has status 1494: {HasEventStatus()}");
        ImGui.Text("Action interval is fixed at 11s.");
        ImGui.Text($"NPC reuse window: {NpcReuseWindowMs / 1000}s");
        ImGui.Checkbox("Require FATE 2042 to be active", ref C.RequireFate2042Active);
        ImGui.Checkbox("Retarget off players", ref C.RetargetOffPlayers);
        ImGui.Checkbox("Target-only mode (do not use actions)", ref C.TargetOnlyMode);
        ImGui.InputInt("Max target distance", ref C.MaxDistance);
        ImGui.InputInt("Retarget throttle (ms)", ref C.RetargetMs);
        ImGui.DragFloat("NPC moved threshold", ref C.NpcMovedThreshold, 0.05f, 0.1f, 5f);

        C.MaxDistance = Math.Clamp(C.MaxDistance, 3, 80);
        C.RetargetMs = Math.Clamp(C.RetargetMs, 100, 5000);
        C.NpcMovedThreshold = Math.Clamp(C.NpcMovedThreshold, 0.1f, 5f);
    }

    public class Config : IEzConfig
    {
        public bool RequireFate2042Active = true;
        public bool RetargetOffPlayers = false;
        public bool TargetOnlyMode = false;
        public int MaxDistance = 35;
        public int RetargetMs = 250;
        public float NpcMovedThreshold = 0.9f;
    }
}
