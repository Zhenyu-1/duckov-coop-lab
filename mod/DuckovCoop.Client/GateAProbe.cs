using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DuckovCoopClient
{
    /// <summary>
    /// Gate A diagnostic probe. Enabled only by CoopRuntime.GateA config/sentinel.
    /// It logs native hurt/explosion/death paths, provides a synthetic apply test,
    /// and can build the minimal A4/A11 hit surfaces. It never sends wire data and
    /// never decides damage; it is a temporary capability probe.
    /// </summary>
    internal static class GateAProbe
    {
        private const string HarmonyId = "duckovcoop.gatea";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object _harmony;
        private static bool _patched;
        private static bool _skipNextNativeHurt;
        private static float _authoritativeUntil;
        private static int _hurtSequence;
        private static int _explosionSequence;
        private static int _zoneDiagCount;
        private static int _aimDiagCount;
        private static int _meleeDiagCount;

        internal static bool Enabled { get; set; }
        internal static bool FreezeAiGhosts { get; set; }

        internal static void Init()
        {
            if (_patched)
                return;

            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(GateAProbe).Assembly);
                _harmony = harmony;
                _patched = true;
                CoopRuntime.Log("[gatea] Harmony patches applied (Hurt/Health/Explosion/OnDead)");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] patch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Dispose()
        {
            try
            {
                if (_harmony is Harmony harmony)
                    harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] unpatch failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                _harmony = null;
                _patched = false;
                FreezeAiGhosts = false;
            }
        }

        internal static void BeginAuthoritativeApply()
        {
            _authoritativeUntil = Time.unscaledTime + 1f;
        }

        internal static bool ConsumeAuthoritativeApply()
        {
            if (!Enabled || Time.unscaledTime > _authoritativeUntil)
                return false;

            _authoritativeUntil = 0f;
            return true;
        }

        internal static void ApplySynthetic(Health health, DamageReceiver receiver,
            CharacterMainControl source, bool skipNative)
        {
            if (health == null)
            {
                CoopRuntime.Log("[gatea] A5/A6 synthetic skipped: no Health");
                return;
            }

            var before = health.CurrentHealth;
            var point = health.transform.position + Vector3.up * 1.0f;
            var info = new DamageInfo
            {
                damageValue = 1f,
                finalDamage = 1f,
                critDamageFactor = 1.5f,
                damagePoint = point,
                damageNormal = Vector3.up,
                fromCharacter = source,
                toDamageReceiver = receiver,
                fromWeaponItemID = 0,
                isExplosion = false,
                elementFactors = new List<ElementFactor>(),
            };

            if (skipNative)
                _skipNextNativeHurt = true;

            BeginAuthoritativeApply();
            var handled = receiver != null ? receiver.Hurt(info) : health.Hurt(info);
            var after = health.CurrentHealth;
            CoopRuntime.Log($"[gatea] A5/A6 synthetic skip={skipNative} handled={handled} " +
                            $"before={before:F0} after={after:F0} " +
                            $"path={(receiver != null ? "DamageReceiver.Hurt" : "Health.Hurt")}");
        }

        internal static void ConfigureAiGhost(GameObject ghost, uint aiId)
        {
            if (ghost == null || ghost.GetComponent<GateAAiSensorMarker>() != null)
                return;
            if (ghost.GetComponent<CharacterMainControl>() != null ||
                ghost.GetComponent<RemoteAIReplicaTag>() != null)
                return;

            try
            {
                var collider = ghost.GetComponent<Collider>();
                if (collider == null)
                    collider = ghost.AddComponent<CapsuleCollider>();
                collider.enabled = true;
                if (collider is CapsuleCollider capsule)
                {
                    capsule.center = new Vector3(0f, 0.9f, 0f);
                    capsule.height = 1.8f;
                    capsule.radius = 0.35f;
                }
                collider.isTrigger = false;

                var health = ghost.GetComponent<Health>();
                if (health == null)
                    health = ghost.AddComponent<Health>();
                typeof(Health).GetField("defaultMaxHealth", Any)?.SetValue(health, 1000000);
                typeof(Health).GetField("DestroyOnDead", Any)?.SetValue(health, false);
                health.autoInit = true;
                health.SetInvincible(true);
                health.team = Teams.scav;
                health.Init();

                var receiver = ghost.GetComponent<DamageReceiver>();
                if (receiver == null)
                    receiver = ghost.AddComponent<DamageReceiver>();
                receiver.health = health;
                receiver.useSimpleHealth = false;

                var damageLayer = LayerMask.NameToLayer("DamageReceiver");
                if (damageLayer >= 0)
                    ghost.layer = damageLayer;

                var marker = ghost.AddComponent<GateAAiSensorMarker>();
                marker.AiId = aiId;
                var character = ghost.GetComponent<CharacterMainControl>();
                var aiController = ghost.GetComponent<AICharacterController>();
                CoopRuntime.Log($"[gatea] A11 AI sensor ready ai={aiId} root={ghost.name} " +
                                $"team={health.team} layer={ghost.layer} " +
                                $"collider={(collider != null ? collider.GetType().Name : "none")} " +
                                $"hp={health.MaxHealth:F0} character={(character != null)} " +
                                $"aiController={(aiController != null)} receiver={receiver.enabled}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] A11 sensor failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Tick(CoopRuntime runtime)
        {
            if (!Enabled || runtime == null)
                return;

            if (Input.GetKeyDown(KeyCode.F7))
                runtime.GateALogStatus();
            if (Input.GetKeyDown(KeyCode.F8))
                runtime.GateATestApply(false);
            if (Input.GetKeyDown(KeyCode.F9))
                runtime.GateATestApply(true);
            if (Input.GetKeyDown(KeyCode.F10))
                runtime.GateAStartHeadshotScan();
            if (Input.GetKeyDown(KeyCode.F12))
                runtime.GateADumpAiComponents();
        }

        internal static void LogHurt(string path, DamageReceiver receiver, DamageInfo info)
        {
            if (!Enabled)
                return;

            try
            {
                var target = receiver ?? info.toDamageReceiver;
                var source = info.fromCharacter;
                var targetHealth = target != null ? target.health : null;
                var hp = targetHealth != null ? targetHealth.CurrentHealth : -1f;
                var sourceName = source != null ? source.name : "<null>";
                var sourceTeam = "?";
                try { if (source != null) sourceTeam = source.Team.ToString(); } catch { }
                var targetTeam = "?";
                try { if (target != null) targetTeam = target.Team.ToString(); } catch { }
                var zone = DeriveZone(info, target);
                var seq = ++_hurtSequence;
                LogZoneDiagnostics(target, info.damagePoint);
                LogAimHeadDiagnostics(target, info.damagePoint);

                CoopRuntime.Log($"[gatea] A1/A2 hurt seq={seq} path={path} " +
                                $"dmg={info.damageValue:F1} final={info.finalDamage:F1} " +
                                $"crit={info.crit} critRate={info.critRate:F2} " +
                                $"ap={info.armorPiercing:F2} weapon={info.fromWeaponItemID} " +
                                $"dmgType={info.damageType} explosion={info.isExplosion} " +
                                $"point=({info.damagePoint.x:F2},{info.damagePoint.y:F2},{info.damagePoint.z:F2}) " +
                                $"normal=({info.damageNormal.x:F2},{info.damageNormal.y:F2},{info.damageNormal.z:F2}) " +
                                $"source={sourceName} sourceTeam={sourceTeam} " +
                                $"target={(target != null ? target.name : "<null>")} targetTeam={targetTeam} " +
                                $"hp={hp:F0} zone={zone}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] hurt log failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void LogZoneDiagnostics(DamageReceiver receiver, Vector3 point)
        {
            if (receiver == null || _zoneDiagCount >= 6)
                return;

            _zoneDiagCount++;
            try
            {
                var headColliders = receiver.GetComponentsInChildren<HeadCollider>(true);
                CoopRuntime.Log($"[gatea] A2 diag target={receiver.name} hit={GateAVector(point)} " +
                                $"headColliders={headColliders.Length}");

                foreach (var head in headColliders)
                {
                    if (head == null)
                        continue;
                    var sphere = head.GetComponent<SphereCollider>() ??
                                 head.GetComponentInChildren<SphereCollider>(true);
                    var closest = sphere != null ? sphere.ClosestPoint(point) : head.transform.position;
                    CoopRuntime.Log($"[gatea] A2 diag head path={GateAPath(head.transform)} " +
                                    $"center={GateAVector(head.transform.position)} " +
                                    $"sphere={(sphere != null)} closest={GateAVector(closest)} " +
                                    $"dist={Vector3.Distance(closest, point):F2}");
                }

                foreach (var collider in receiver.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null)
                        continue;

                    var closest = collider.ClosestPoint(point);
                    var distance = Vector3.Distance(closest, point);
                    var isHead = collider.GetComponent<HeadCollider>() != null;
                    string bounds;
                    try
                    {
                        bounds = $"{GateAVector(collider.bounds.center)}/{GateAVector(collider.bounds.size)}";
                    }
                    catch
                    {
                        bounds = "?";
                    }

                    CoopRuntime.Log($"[gatea] A2 diag collider path={GateAPath(collider.transform)} " +
                                    $"type={collider.GetType().Name} enabled={collider.enabled} " +
                                    $"active={collider.gameObject.activeInHierarchy} trigger={collider.isTrigger} " +
                                    $"layer={collider.gameObject.layer} head={isHead} " +
                                    $"closest={GateAVector(closest)} dist={distance:F2} bounds={bounds}");
                }
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] A2 diag failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string GateAVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private static string GateAPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var parts = new List<string>();
            var current = transform;
            for (var i = 0; i < 5 && current != null; i++)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void LogAimHeadDiagnostics(DamageReceiver receiver, Vector3 point)
        {
            if (receiver == null || _aimDiagCount >= 30)
                return;

            _aimDiagCount++;
            try
            {
                var character = receiver.GetComponentInParent<CharacterMainControl>();
                var headColliders = character != null
                    ? character.GetComponentsInChildren<HeadCollider>(true)
                    : receiver.GetComponentsInChildren<HeadCollider>(true);
                var input = UnityEngine.Object.FindObjectOfType<InputManager>();
                var aimingHead = input != null && input.AimingEnemyHead;
                var hittedHeadName = "<null>";
                var hittedHeadPoint = Vector3.zero;
                if (input != null)
                {
                    var raw = AccessTools.Field(typeof(InputManager), "hittedHead")?.GetValue(input);
                    if (raw is RaycastHit hit)
                    {
                        hittedHeadName = hit.collider != null ? hit.collider.name : "<null>";
                        hittedHeadPoint = hit.point;
                    }
                }

                var names = new List<string>();
                foreach (var head in headColliders)
                    if (head != null)
                        names.Add(head.name);

                CoopRuntime.Log($"[gatea] A2 aim headColliders={headColliders.Length} " +
                                $"names={string.Join(",", names)} aimingHead={aimingHead} " +
                                $"hittedHead={hittedHeadName} " +
                                $"hitPoint={GateAVector(point)} hittedPoint={GateAVector(hittedHeadPoint)}");
            }
            catch (Exception ex)
            {
                CoopRuntime.Log("[gatea] A2 aim diag failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string DeriveZone(DamageInfo info, DamageReceiver receiver)        {
            try
            {
                if (receiver == null)
                    return "body";

                var point = info.damagePoint;
                var head = receiver.GetComponentInChildren<HeadCollider>(true);
                var headCollider = head != null
                    ? head.GetComponent<SphereCollider>() ?? head.GetComponentInChildren<SphereCollider>(true)
                    : null;
                if (headCollider != null)
                {
                    var closest = headCollider.ClosestPoint(point);
                    var distance = Vector3.Distance(closest, point);
                    if (distance <= 0.25f)
                        return $"head(headCollider d={distance:F2})";
                }

                Collider best = null;
                var bestDistance = float.MaxValue;
                foreach (var collider in receiver.GetComponentsInChildren<Collider>(true))
                {
                    if (collider == null || !collider.enabled)
                        continue;

                    var closest = collider.ClosestPoint(point);
                    var distance = Vector3.Distance(closest, point);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = collider;
                    }
                }

                if (best == null)
                    return "body(noCollider)";

                var name = best.name.ToLowerInvariant();
                var zone = name.Contains("head")
                    ? "head"
                    : name.Contains("leg") || name.Contains("foot") || name.Contains("arm") || name.Contains("hand")
                        ? "limb"
                        : "body";
                return $"{zone}({best.name} d={bestDistance:F2})";
            }
            catch
            {
                return "body(error)";
            }
        }

        [HarmonyPatch(typeof(DamageReceiver), "Hurt")]
        private static class DamageReceiverHurtPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(DamageReceiver __instance, DamageInfo damageInfo)
            {
                if (!Enabled)
                    return true;

                LogHurt("DamageReceiver.Hurt", __instance, damageInfo);
                CoopRuntime.PlayProvisionalHitFeedback(damageInfo);
                if (__instance != null && __instance.GetComponentInParent<RemoteAIReplicaTag>() != null)
                {
                    CoopRuntime.Log("[gatea] A11 replica local Hurt suppressed");
                    return false;
                }
                if (_skipNextNativeHurt)
                {
                    _skipNextNativeHurt = false;
                    CoopRuntime.Log("[gatea] A5 native DamageReceiver.Hurt skipped by probe");
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(Health), "Hurt")]
        private static class HealthHurtPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Health __instance, DamageInfo damageInfo, ref float __state)
            {
                __state = __instance != null ? __instance.CurrentHealth : 0f;
                if (!Enabled)
                    return true;

                LogHurt("Health.Hurt", __instance != null ? __instance.GetComponent<DamageReceiver>() : null,
                    damageInfo);
                if (__instance != null && __instance.GetComponentInParent<RemoteAIReplicaTag>() != null)
                {
                    CoopRuntime.Log("[gatea] A11 replica local Health.Hurt suppressed");
                    return false;
                }
                if (_skipNextNativeHurt)
                {
                    _skipNextNativeHurt = false;
                    CoopRuntime.Log("[gatea] A5 native Health.Hurt skipped by probe");
                    return false;
                }
                return true;
            }

            [HarmonyPostfix]
            private static void Postfix(Health __instance, DamageInfo damageInfo, bool __result, float __state)
            {
                if (!Enabled || __instance == null)
                    return;

                try
                {
                    if (__result)
                        LogCalibration(__instance, damageInfo, __state);

                    if (__instance.IsDead || __instance.CurrentHealth <= 0f)
                    {
                        CoopRuntime.Log($"[gatea] A9 health hurt death health={__instance.name} " +
                                        $"result={__result} hp={__instance.CurrentHealth:F0} dead={__instance.IsDead} " +
                                        $"dmg={damageInfo.damageValue:F1} final={damageInfo.finalDamage:F1} " +
                                        $"weapon={damageInfo.fromWeaponItemID}");
                    }
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[gatea] A9 health death log failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            /// <summary>Native-finalDamage calibration sample for CORE's formula checks.</summary>
            private static void LogCalibration(Health health, DamageInfo info, float hpBefore)
            {
                var elements = new System.Text.StringBuilder();
                foreach (var element in info.elementFactors)
                {
                    if (elements.Length > 0)
                        elements.Append(';');
                    elements.Append(element.elementType).Append(':')
                        .Append(element.factor.ToString("F2")).Append('x')
                        .Append(health.ElementFactor(element.elementType).ToString("F2"));
                }

                var armorFactor = "n/a";
                try
                {
                    var ruleProperty = typeof(LevelManager).GetProperty("Rule",
                        BindingFlags.Public | BindingFlags.Static);
                    var rule = ruleProperty?.GetValue(null);
                    var field = rule?.GetType().GetField("damageFactor_ToPlayer",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null)
                        armorFactor = Convert.ToString(field.GetValue(rule), CultureInfo.InvariantCulture);
                }
                catch
                {
                    // rules may not be ready in the base scene
                }

                CoopRuntime.Log($"[gatea] A1/A2 result final={info.finalDamage:F2} " +
                                $"hp={hpBefore:F1}->{health.CurrentHealth:F1} " +
                                $"dmg={info.damageValue:F2} crit={info.crit} critRate={info.critRate:F2} " +
                                $"dmgType={info.damageType} ap={info.armorPiercing:F2} " +
                                $"armorBody={health.BodyArmor:F2} armorHead={health.HeadArmor:F2} " +
                                $"elements=[{elements}] diff={armorFactor} " +
                                $"weapon={info.fromWeaponItemID} explosion={info.isExplosion} " +
                                $"team={health.team}");
            }
        }

        [HarmonyPatch(typeof(DamageReceiver), "OnDead")]
        private static class DamageReceiverDeadPatch
        {
            [HarmonyPrefix]
            private static void Prefix(DamageReceiver __instance, DamageInfo dmgInfo)
            {
                if (!Enabled)
                    return;

                CoopRuntime.Log($"[gatea] A9 native OnDead target={(__instance != null ? __instance.name : "<null>")} " +
                                $"dmg={dmgInfo.damageValue:F1} final={dmgInfo.finalDamage:F1} " +
                                $"weapon={dmgInfo.fromWeaponItemID}");
            }
        }

        private static bool IsHeadshotBuffName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            var lower = name.ToLowerInvariant();
            return lower.Contains("headshot") || lower.Contains("focused") || lower.Contains("chargedhead");
        }

        [HarmonyPatch(typeof(Health), "AddBuff")]
        private static class HealthAddBuffPatch
        {
            [HarmonyPrefix]
            private static void Prefix(Health __instance, Duckov.Buffs.Buff buffPfb,
                CharacterMainControl fromWho, int overrideFromWeaponID)
            {
                if (!Enabled || buffPfb == null)
                    return;

                try
                {
                    var name = buffPfb.name ?? string.Empty;
                    if (!IsHeadshotBuffName(name) && !name.Contains("Crit") && !name.Contains("crit"))
                        return;

                    CoopRuntime.Log($"[gatea] A2fx health addbuff t={Time.unscaledTime:F3} " +
                                    $"health={(__instance != null ? __instance.name : "<null>")} " +
                                    $"buff={name} from={(fromWho != null ? fromWho.name : "<null>")} " +
                                    $"weapon={overrideFromWeaponID}");
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[gatea] A2fx health addbuff log failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Duckov.Buffs.Buff), "Setup")]
        private static class BuffSetupPatch
        {
            [HarmonyPrefix]
            private static void Prefix(Duckov.Buffs.Buff __instance, Duckov.Buffs.CharacterBuffManager manager)
            {
                if (!Enabled || __instance == null || !IsHeadshotBuffName(__instance.name))
                    return;

                var master = AccessTools.Field(typeof(Duckov.Buffs.CharacterBuffManager), "master")
                    ?.GetValue(manager) as CharacterMainControl;
                CoopRuntime.Log($"[gatea] A2fx buff setup t={Time.unscaledTime:F3} " +
                                $"buff={__instance.name} manager={(master != null ? master.name : "<null>")}");
            }
        }

        [HarmonyPatch(typeof(UnityEngine.Object), "Instantiate", new[] { typeof(UnityEngine.Object) })]
        private static class InstantiatePatch
        {
            [HarmonyPrefix]
            private static void Prefix(UnityEngine.Object original)
            {
                if (!Enabled || original == null)
                    return;

                var name = original.name ?? string.Empty;
                if (!IsHeadshotBuffName(name) && !name.Contains("Crit") && !name.Contains("crit"))
                    return;

                CoopRuntime.Log($"[gatea] A2fx instantiate t={Time.unscaledTime:F3} name={name} " +
                                $"type={original.GetType().Name}");
            }
        }

        [HarmonyPatch(typeof(Duckov.Buffs.CharacterBuffManager), "AddBuff")]
        private static class BuffAddPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Duckov.Buffs.CharacterBuffManager __instance, Duckov.Buffs.Buff buffPrefab,
                CharacterMainControl fromWho, int overrideWeaponID)
            {
                // A null prefab is an invalid native add (spider/replica effect triggers can
                // hit this every frame). Skip it instead of letting Buff.Setup throw NREs.
                if (buffPrefab == null)
                    return false;
                if (!Enabled)
                    return true;

                try
                {
                    var name = buffPrefab.name ?? string.Empty;
                    var id = AccessTools.Field(typeof(Duckov.Buffs.Buff), "id")?.GetValue(buffPrefab);
                    if (!IsHeadshotBuffName(name) && !name.Contains("Crit") && !name.Contains("crit"))
                        return true;

                    var master = AccessTools.Field(typeof(Duckov.Buffs.CharacterBuffManager), "master")
                        ?.GetValue(__instance) as CharacterMainControl;
                    CoopRuntime.Log($"[gatea] A2fx buff add t={Time.unscaledTime:F3} " +
                                    $"master={(master != null ? master.name : "<null>")} " +
                                    $"buff={name} id={id} from={(fromWho != null ? fromWho.name : "<null>")} " +
                                    $"weapon={overrideWeaponID}");
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[gatea] A2fx buff log failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(ItemAgent_MeleeWeapon), "CheckAndDealDamage")]
        private static class MeleeDamagePatch
        {
            [HarmonyPrefix]
            private static void Prefix(ItemAgent_MeleeWeapon __instance)
            {
                if (!Enabled || __instance == null || _meleeDiagCount >= 20)
                    return;

                _meleeDiagCount++;
                CoopRuntime.Log($"[gatea] A11 melee attack weapon={__instance.name} " +
                                $"damage={__instance.Damage:F1} range={__instance.AttackRange:F2}");
            }

            [HarmonyPostfix]
            private static void Postfix(ItemAgent_MeleeWeapon __instance)
            {
                if (!Enabled || __instance == null || _meleeDiagCount > 20)
                    return;

                var colliders = AccessTools.Field(typeof(ItemAgent_MeleeWeapon), "colliders")
                    ?.GetValue(__instance) as Collider[];
                CoopRuntime.Log($"[gatea] A11 melee done weapon={__instance.name} " +
                                $"colliders={(colliders != null ? colliders.Length : -1)}");
            }
        }

        [HarmonyPatch(typeof(ItemAgent_MeleeWeapon), "CheckCollidersInRange")]
        private static class MeleeRangePatch
        {
            [HarmonyPostfix]
            private static void Postfix(ItemAgent_MeleeWeapon __instance, bool dealDamage, int __result)
            {
                if (!Enabled || __instance == null || _meleeDiagCount > 20)
                    return;

                if (!dealDamage && __result <= 0)
                    return;

                CoopRuntime.Log($"[gatea] A11 melee range weapon={__instance.name} " +
                                $"dealDamage={dealDamage} result={__result} " +
                                $"range={__instance.AttackRange:F2}");
            }
        }

        [HarmonyPatch(typeof(Grenade), "Explode")]
        private static class GrenadeExplodePatch
        {            [HarmonyPrefix]
            private static void Prefix(Grenade __instance)
            {
                if (!Enabled || __instance == null)
                    return;

                try
                {
                    var canHurtSelf = AccessTools.Field(typeof(Grenade), "canHurtSelf")?.GetValue(__instance);
                    var selfTeam = AccessTools.Field(typeof(Grenade), "selfTeam")?.GetValue(__instance);
                    CoopRuntime.Log($"[gatea] A8 grenade explode name={__instance.name} " +
                                    $"createExplosion={__instance.createExplosion} " +
                                    $"createOnExplode={(__instance.createOnExlode != null ? __instance.createOnExlode.name : "<null>")} " +
                                    $"range={__instance.damageRange:F2} " +
                                    $"canHurtSelf={canHurtSelf} selfTeam={selfTeam} " +
                                    $"dmgExplosion={(__instance.damageInfo.isExplosion)}");
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[gatea] A8 grenade diag failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(ExplosionManager), "CreateExplosion")]
        private static class ExplosionPatch
        {            [HarmonyPrefix]
            private static void Prefix(Vector3 center, float radius, DamageInfo dmgInfo)
            {
                if (!Enabled)
                    return;

                _explosionSequence++;
                CoopRuntime.Log($"[gatea] A8 explosion seq={_explosionSequence} " +
                                $"center=({center.x:F2},{center.y:F2},{center.z:F2}) radius={radius:F2} " +
                                $"dmg={dmgInfo.damageValue:F1} final={dmgInfo.finalDamage:F1} " +
                                $"weapon={dmgInfo.fromWeaponItemID} explosionFlag={dmgInfo.isExplosion}");
            }

            [HarmonyPostfix]
            private static void Postfix(ExplosionManager __instance)
            {
                if (!Enabled)
                    return;

                try
                {
                    var list = AccessTools.Field(typeof(ExplosionManager), "damagedHealth")?.GetValue(__instance) as IList;
                    CoopRuntime.Log($"[gatea] A8 explosion victims={(list != null ? list.Count : -1)}");
                }
                catch (Exception ex)
                {
                    CoopRuntime.Log("[gatea] A8 victim count failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }

    /// <summary>Marks an AI ghost that already carries the A11 hit surface.</summary>
    internal sealed class GateAAiSensorMarker : MonoBehaviour
    {
        internal uint AiId;
    }
}
