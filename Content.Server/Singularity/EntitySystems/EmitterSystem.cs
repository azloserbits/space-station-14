using System.Numerics;
using System.Threading;
using Content.Server.Administration.Logs;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Projectiles;
using Content.Server.Pinpointer;
using Content.Server.Radio.EntitySystems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Construction;
using Content.Shared.Database;
using Content.Shared.Destructible;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Interaction;
using Content.Shared.Lock;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Projectiles;
using Content.Shared.Singularity.Components;
using Content.Shared.Singularity.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Utility;
using Timer = Robust.Shared.Timing.Timer;
using Robust.Shared.Timing;

namespace Content.Server.Singularity.EntitySystems
{
    public sealed partial class EmitterSystem : SharedEmitterSystem
    {
        [Dependency] private IRobustRandom _random = default!;
        [Dependency] private IAdminLogManager _adminLogger = default!;
        [Dependency] private SharedAppearanceSystem _appearance = default!;
        [Dependency] private SharedPopupSystem _popup = default!;
        [Dependency] private ProjectileSystem _projectile = default!;
        [Dependency] private GunSystem _gun = default!;
        [Dependency] private RadioSystem _radio = default!;
        [Dependency] private NavMapSystem _navMap = default!;
        [Dependency] private IGameTiming _gameTiming = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<EmitterComponent, PowerConsumerReceivedChanged>(ReceivedChanged);
            SubscribeLocalEvent<EmitterComponent, PowerChangedEvent>(OnApcChanged);
            SubscribeLocalEvent<EmitterComponent, ActivateInWorldEvent>(OnActivate);
            SubscribeLocalEvent<EmitterComponent, AnchorStateChangedEvent>(OnAnchorStateChanged);
            SubscribeLocalEvent<EmitterComponent, SignalReceivedEvent>(OnSignalReceived);
            SubscribeLocalEvent<EmitterComponent, DestructionEventArgs>(OnDestruction);
            SubscribeLocalEvent<EmitterComponent, MachineDeconstructedEvent>(OnDeconstructed); // you shouldn't be able to deconstruct locked emitters but out of scope to fix
            SubscribeLocalEvent<EmitterComponent, LockToggledEvent>(OnLockToggled);
        }

        private void OnAnchorStateChanged(Entity<EmitterComponent> ent, ref AnchorStateChangedEvent args)
        {
            if (args.Anchored)
                return;

            SwitchOff(ent);
        }

        private void OnActivate(Entity<EmitterComponent> ent, ActivateInWorldEvent args)
        {
            var (uid, comp) = ent;
            if (args.Handled || !args.Complex)
                return;

            if (TryComp(uid, out LockComponent? lockComp) && lockComp.Locked)
            {
                _popup.PopupEntity(Loc.GetString("comp-emitter-access-locked",
                    ("target", uid)), uid, args.User);
                return;
            }

            if (TryComp(uid, out PhysicsComponent? phys) && phys.BodyType == BodyType.Static)
            {
                if (!comp.IsOn)
                {
                    SwitchOn(ent);
                    _popup.PopupEntity(Loc.GetString("comp-emitter-turned-on",
                        ("target", uid)), uid, args.User);
                }
                else
                {
                    SwitchOff(ent);
                    _popup.PopupEntity(Loc.GetString("comp-emitter-turned-off",
                        ("target", uid)), uid, args.User);
                }

                var stateText = comp.IsOn ? "on" : "off";
                _adminLogger.Add(LogType.FieldGeneration,
                    comp.IsOn ? LogImpact.Medium : LogImpact.High,
                    $"{ToPrettyString(args.User):player} toggled {ToPrettyString(uid):emitter} to {stateText}");
                args.Handled = true;
            }
            else
            {
                _popup.PopupEntity(Loc.GetString("comp-emitter-not-anchored",
                    ("target", uid)), uid,args.User);
            }
        }

        private void ReceivedChanged(
            Entity<EmitterComponent> ent,
            ref PowerConsumerReceivedChanged args)
        {
            var comp = ent.Comp;
            if (!comp.IsOn)
            {
                return;
            }

            if (args.ReceivedPower < args.DrawRate)
            {
                PowerOff(ent);
            }
            else
            {
                PowerOn(ent);
            }
        }

        private void OnApcChanged(Entity<EmitterComponent> ent, ref PowerChangedEvent args)
        {
            var comp = ent.Comp;
            if (!comp.IsOn)
            {
                return;
            }

            if (!args.Powered)
            {
                PowerOff(ent);
            }
            else
            {
                PowerOn(ent);
            }
        }

        public void SwitchOff(Entity<EmitterComponent> ent)
        {
            var (uid, comp) = ent;
            comp.IsOn = false;
            if (TryComp<PowerConsumerComponent>(uid, out var powerConsumer))
                powerConsumer.DrawRate = 1; // this needs to be not 0 so that the visuals still work.
            if (TryComp<ApcPowerReceiverComponent>(uid, out var apcReceiver))
                apcReceiver.Load = 1;
            PowerOff(ent);
            UpdateAppearance(ent);
        }

        public void SwitchOn(Entity<EmitterComponent> ent)
        {
            var (uid, comp) = ent;
            comp.IsOn = true;
            if (TryComp<PowerConsumerComponent>(uid, out var powerConsumer))
                powerConsumer.DrawRate = comp.PowerUseActive;
            if (TryComp<ApcPowerReceiverComponent>(uid, out var apcReceiver))
            {
                apcReceiver.Load = comp.PowerUseActive;
                if (apcReceiver.Powered)
                    PowerOn(ent);
            }
            // Do not directly PowerOn().
            // OnReceivedPowerChanged will get fired due to DrawRate change which will turn it on.
            UpdateAppearance(ent);
        }

        public void PowerOff(Entity<EmitterComponent> ent)
        {
            var comp = ent.Comp;
            if (!comp.IsPowered)
            {
                return;
            }

            ScheduleAlert((ent), comp.LocUnpowered);
            comp.IsPowered = false;
            UpdateAppearance(ent);
        }

        public void PowerOn(Entity<EmitterComponent> ent)
        {
            var comp = ent.Comp;
            if (comp.IsPowered)
            {
                return;
            }

            comp.IsPowered = true;

            comp.FireShotCounter = 0;
            comp.TimerCancel = new CancellationTokenSource();

            //Timer.Spawn(component.FireBurstDelayMax, () => ShotTimerCallback(ent), component.TimerCancel.Token);

            UpdateAppearance(ent);
        }

        private void ShotTimerCallback(Entity<EmitterComponent> ent)
        {
            var (uid, comp) = ent;

            if (comp.Deleted)
                return;

            // Any power-off condition should result in the timer for this method being cancelled
            // and thus not firing
            DebugTools.Assert(comp.IsPowered);
            DebugTools.Assert(comp.IsOn);

            Fire(ent);

            TimeSpan delay;
            if (comp.FireShotCounter < comp.FireBurstSize)
            {
                comp.FireShotCounter += 1;
                delay = comp.FireInterval;
            }
            else
            {
                comp.FireShotCounter = 0;
                var diff = comp.FireBurstDelayMax - comp.FireBurstDelayMin;
                // TIL you can do TimeSpan * double.
                delay = comp.FireBurstDelayMin + _random.NextFloat() * diff;
            }

            // Must be set while emitter powered.
            DebugTools.AssertNotNull(comp.TimerCancel);
            Timer.Spawn(delay, () => ShotTimerCallback(ent), comp.TimerCancel!.Token);
        }

        private void Fire(Entity<EmitterComponent> ent)
        {
            var (uid, comp) = ent;

            if (!TryComp<GunComponent>(uid, out var gunComponent))
                return;

            var xform = Transform(uid);
            //shooteruid is the temporary variable name because idk what to name it
            var Shooteruid = Spawn(comp.BoltType, xform.Coordinates);
            var proj = EnsureComp<ProjectileComponent>(Shooteruid);
            _projectile.SetShooter(uid, proj, Shooteruid);

            var targetPos = new EntityCoordinates(uid, new Vector2(0, -1));

            _gun.Shoot((uid, gunComponent), ent, xform.Coordinates, targetPos, out _);
        }

        private void UpdateAppearance(Entity<EmitterComponent> ent)
        {
            var (uid, comp) = ent;

            EmitterVisualState state;
            if (comp.IsPowered)
            {
                state = EmitterVisualState.On;
            }
            else if (comp.IsOn)
            {
                state = EmitterVisualState.Underpowered;
            }
            else
            {
                state = EmitterVisualState.Off;
            }
            _appearance.SetData(uid, EmitterVisuals.VisualState, state);
        }

        private void OnSignalReceived(Entity<EmitterComponent> ent, ref SignalReceivedEvent args)
        {
            var (uid, comp) = ent;
            // must anchor the emitter for signals to work
            if (TryComp<PhysicsComponent>(uid, out var phys) && phys.BodyType != BodyType.Static)
                return;

            if (args.Port == comp.OffPort)
            {
                SwitchOff(ent);
            }
            else if (args.Port == comp.OnPort)
            {
                SwitchOn(ent);
            }
            else if (args.Port == comp.TogglePort)
            {
                if (comp.IsOn)
                {
                    SwitchOff(ent);
                }
                else
                {
                    SwitchOn(ent);
                }
            }
            else if (comp.SetTypePorts.TryGetValue(args.Port, out var boltType))
            {
                comp.BoltType = boltType;
            }
        }

        private void OnDestruction(Entity<EmitterComponent> ent, ref DestructionEventArgs args)
        {
            // Engineering needs to know if an emitter is destroyed so they can replace it before the engine looses.
            AlertRadio(ent, ent.Comp.LocDestroyed);
        }

        private void OnDeconstructed(Entity<EmitterComponent> ent, ref MachineDeconstructedEvent args)
        {
            // right now you don't even need to unlock the emitter to deconstruct it. that's almost certainly a bug but even without it it probably still needs an alert
            ScheduleAlert(ent, ent.Comp.LocDeconstructed);
        }

        private void AlertRadio(Entity<EmitterComponent> ent, string locString)
        {
            if (!ent.Comp.AlertRadio || !ent.Comp.IsOn || !ent.Comp.IsPowered)
                return; // APEs do not need to scream over engineering radio, and an emitter that is off is probably not going to be alerting radios

            var message = Loc.GetString(
                locString,
                ("location", FormattedMessage.RemoveMarkupOrThrow(_navMap.GetNearestBeaconString(ent.Owner)))
            );
            _radio.SendRadioMessage(ent.Owner, message, ent.Comp.RadioChannel, ent.Owner);
        }

        private void ScheduleAlert(Entity<EmitterComponent> ent, string type)
        {
            ent.Comp.AlertData = new AlertData(_gameTiming.CurTime + ent.Comp.AlertUpdateInterval, type, false); //note false is not thingy rn
        }
        private void ClearAlert(Entity<EmitterComponent> ent, string type)
        {
            if (ent.Comp.AlertData?.Message != type)
                return;
            ent.Comp.AlertData = null;
        }

        private void OnLockToggled(Entity<EmitterComponent> ent, ref LockToggledEvent args)
        {
            if (args.Locked)
                return;

            ScheduleAlert(ent, ent.Comp.LocUnlocked);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            var query = EntityQueryEnumerator<EmitterComponent>();
            while (query.MoveNext(out var uid, out var emitter))
            {
                if (emitter.IsOn)
                {
                    var ent = (uid, emitter);
                    ShotTimerCallback(ent);
                    //TODO: add a check for if the delay between bursts happened
                    continue;
                }

                if (emitter.AlertData == null)
                    continue;
                if(_gameTiming.CurTime < emitter.AlertData.AlertTime) continue;

                AlertRadio((uid, emitter), emitter.AlertData.Message);
                emitter.AlertData = null;
            }
        }
    }
}
