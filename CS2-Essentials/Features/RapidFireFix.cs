using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using CS2_CustomVotes.Shared.Models;
using CSSharpUtils.Extensions;
using CSSharpUtils.Utils;
using hvhgg_essentials.Enums;

namespace hvhgg_essentials.Features;

public class RapidFire
{
    private readonly Dictionary<uint, int> _lastPlayerShotTick = new();
    private readonly HashSet<uint> _rapidFireBlockUserIds = new();
    private readonly Dictionary<uint, float> _rapidFireBlockWarnings = new();

    // Для бурста (2 выстрела) в режиме Allow
    private readonly Dictionary<uint, float> _lastBurstShotTime = new();
    private readonly Dictionary<uint, int> _burstShotCount = new();
    private const float BURST_TIMEOUT = 0.2f; // 200 мс между сериями

    private readonly Plugin _plugin;
    public static readonly FakeConVar<int> hvh_restrict_rapidfire = new("hvh_restrict_rapidfire", "Restrict rapid fire", 0, ConVarFlags.FCVAR_REPLICATED, new RangeValidator<int>(0, 3));
    public static readonly FakeConVar<float> hvh_rapidfire_reflect_scale = new("hvh_rapidfire_reflect_scale", "Reflect scale", 1, ConVarFlags.FCVAR_REPLICATED, new RangeValidator<float>(0, 1));

    public RapidFire(Plugin plugin)
    {
        _plugin = plugin;
        _plugin.RegisterFakeConVars(this);
        hvh_restrict_rapidfire.Value = (int) _plugin.Config.RapidFireFixMethod;
        hvh_rapidfire_reflect_scale.Value = _plugin.Config.RapidFireReflectScale;
    }

    public static void RegisterCustomVotes(Plugin plugin)
    {
        var defaultOption = plugin.Config.RapidFireFixMethod switch
        {
            FixMethod.Allow => "Allow",
            FixMethod.Ignore => "Block",
            FixMethod.Reflect => "Reflect",
            FixMethod.ReflectSafe => "Reflect (safe)",
            _ => "Allow"
        };

        var options = new Dictionary<string, VoteOption>();

        if (plugin.Config.CustomVoteSettings.RapidFireVote != "off")
        {
            options.Add("Allow", new VoteOption("{Green}Allow", new List<string> { "hvh_restrict_rapidfire 0" }));
            options.Add("Block", new VoteOption("{Red}Block", new List<string> { "hvh_restrict_rapidfire 1" }));
            
            if (plugin.Config.CustomVoteSettings.RapidFireVote == "full")
            {
                options.Add("Reflect", new VoteOption("{Orange}Reflect", new List<string> { "hvh_restrict_rapidfire 2" }));
                options.Add("Reflect (safe)", new VoteOption("{Orange}Reflect (safe)", new List<string> { "hvh_restrict_rapidfire 3" }));
            }
        }
        
        Plugin.CustomVotesApi.Get()?.AddCustomVote(
            "rapidfire", 
            new List<string> {
              "rf"  
            },
            "Rapid fire", 
            defaultOption, 
            30,
            options, 
            plugin.Config.CustomVoteSettings.Style);
    }
    public static void UnregisterCustomVotes(Plugin plugin)
    {
        Plugin.CustomVotesApi.Get()?.RemoveCustomVote("rapidfire");
    }

    // ===== ДЕТЕКЦИЯ БЫСТРОЙ СТРЕЛЬБЫ (для Ignore/Reflect/ReflectSafe) =====
    public HookResult OnWeaponFire(EventWeaponFire eventWeaponFire, GameEventInfo info)
    {
        if (!eventWeaponFire.Userid.IsPlayer())
            return HookResult.Continue;
        
        var firedWeapon = eventWeaponFire.Userid!.Pawn.Value?.WeaponServices?.ActiveWeapon.Value;
        var weaponData = firedWeapon?.GetVData<CCSWeaponBaseVData>();
        
        var index = eventWeaponFire.Userid.Pawn.Index;
            
        if (!_lastPlayerShotTick.TryGetValue(index, out var lastShotTick))
        {
            _lastPlayerShotTick[index] = Server.TickCount;
            return HookResult.Continue;
        }
            
        _lastPlayerShotTick[index] = Server.TickCount;
        
        var shotTickDiff = Server.TickCount - lastShotTick;
        
        // ДОПУСК (tolerance) – уменьшает ложные срабатывания
        int tolerance = 2; // можно вынести в конфиг
        var possibleAttackDiff = (weaponData?.CycleTime.Values[0] * 64 ?? 0) - 1 + tolerance;

        // Если разница больше допустимой – стрельба нормальная
        if (shotTickDiff > possibleAttackDiff || 
            firedWeapon?.DesignerName == "weapon_revolver")
            return HookResult.Continue; 

        // Если режим Allow – не блокируем, только детектируем (но не добавляем в чёрный список)
        if (hvh_restrict_rapidfire.Value == (int)FixMethod.Allow)
            return HookResult.Continue;
            
        // Для остальных режимов – добавляем в чёрный список и логируем
        Console.WriteLine($"[HvH.gg] Detected rapid fire from {eventWeaponFire.Userid.PlayerName}");
            
        if (_rapidFireBlockUserIds.Count == 0)
            Server.NextFrame(_rapidFireBlockUserIds.Clear);
            
        _rapidFireBlockUserIds.Add(index);
            
        if (_rapidFireBlockWarnings.TryGetValue(index, out var lastWarningTime) &&
            lastWarningTime + 3 > Server.CurrentTime) 
            return HookResult.Continue;
            
        if (!_plugin.Config.PrintWarnings) 
            return HookResult.Continue;
        
        Server.PrintToChatAll($"{ChatUtils.FormatMessage(_plugin.Config.ChatPrefix)} Player {ChatColors.Red}{eventWeaponFire.Userid.PlayerName}{ChatColors.Default} tried using {ChatColors.Red}rapid fire{ChatColors.Default}!");
        _rapidFireBlockWarnings[index] = Server.CurrentTime;

        return HookResult.Continue;
    }
    
    // ===== ОБРАБОТКА УРОНА (для Ignore/Reflect/ReflectSafe) =====
    public HookResult OnTakeDamage(DynamicHook h)
    {
        var damageInfo = h.GetParam<CTakeDamageInfo>(1);

        if (damageInfo.Attacker.Value == null)
            return HookResult.Continue;

        if (!_rapidFireBlockUserIds.Contains(damageInfo.Attacker.Index))
            return HookResult.Continue;
            
        switch (hvh_restrict_rapidfire.Value)
        {
            case (int)FixMethod.Allow:
                break;
            case (int)FixMethod.Ignore:
                damageInfo.Damage = 0;
                break;
            case (int)FixMethod.Reflect:
            case (int)FixMethod.ReflectSafe:
                damageInfo.Damage *= hvh_rapidfire_reflect_scale.Value;
                h.SetParam<CEntityInstance>(0, damageInfo.Attacker.Value);
                if (hvh_restrict_rapidfire.Value == (int)FixMethod.ReflectSafe)
                    damageInfo.DamageFlags |= TakeDamageFlags_t.DFLAG_PREVENT_DEATH;
                break;
        }

        return HookResult.Changed;
    }

    // ===== РАЗРЕШЕНИЕ БУРСТА (2 ВЫСТРЕЛА) ДЛЯ РЕЖИМА ALLOW =====
    public HookResult OnBulletImpact(EventBulletImpact eventBulletImpact, GameEventInfo info)
    {
        // Проверки на null
        if (eventBulletImpact.Userid == null || eventBulletImpact.Userid.Pawn == null || eventBulletImpact.Userid.Pawn.Value == null)
            return HookResult.Continue;

        var player = eventBulletImpact.Userid;
        var playerPawn = player.Pawn.Value;
        var weaponServices = playerPawn.WeaponServices;
        if (weaponServices == null)
            return HookResult.Continue;

        var firedWeapon = weaponServices.ActiveWeapon.Value;
        if (firedWeapon == null || firedWeapon.DesignerName == "weapon_revolver")
            return HookResult.Continue;

        // Только для режима Allow
        if (hvh_restrict_rapidfire.Value != (int)FixMethod.Allow)
            return HookResult.Continue;

        uint index = player.Index;
        float currentTime = Server.CurrentTime;

        // Проверяем, прошло ли достаточно времени для новой серии
        if (!_lastBurstShotTime.TryGetValue(index, out var lastTime) || currentTime - lastTime > BURST_TIMEOUT)
        {
            // Начало новой серии
            _burstShotCount[index] = 1;
            _lastBurstShotTime[index] = currentTime;
            // Разрешаем первый выстрел (сбрасываем задержку)
            int playerTickBase = (int)player.TickBase;
            firedWeapon.NextPrimaryAttackTick = playerTickBase - 1;
            Utilities.SetStateChanged(firedWeapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
        }
        else
        {
            // Продолжение серии
            _burstShotCount[index]++;
            _lastBurstShotTime[index] = currentTime;
            if (_burstShotCount[index] <= 2)
            {
                // Второй выстрел – разрешаем
                int playerTickBase = (int)player.TickBase;
                firedWeapon.NextPrimaryAttackTick = playerTickBase - 1;
                Utilities.SetStateChanged(firedWeapon, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            }
            // Если > 2 – ничего не делаем, оружие стреляет с обычной задержкой
        }

        return HookResult.Continue;
    }
}
