#region Copyright & License Information
/**
 * Copyright (c) The OpenRA Combined Arms Developers (see CREDITS).
 * This file is part of OpenRA Combined Arms, which is free software.
 * It is made available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of the License,
 * or (at your option) any later version. For more information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.AS.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

/**
 * I tried implementing distance vector routing algorithm
 * but now I think it is an overkill,
 * because they take memory for each actor and they need to eachange information.
 * Also people just sell towers when they are done with it so
 * that makes these overheads less worthy.
 */

namespace OpenRA.Mods.CA.Traits
{
	[Desc("Implements the charge-then-burst attack logic specific to the RA tesla coil.")]
	public class AttackPrismSupportedCAInfo : AttackPrismInfo
	{
		// These props are inherited automatically:
		/*
		[Desc("How many charges this actor has to attack with, once charged.")]
		public readonly int MaxCharges = 1;
		[Desc("Reload time for all charges (in ticks).")]
		public readonly int ReloadDelay = 120;
		[Desc("Delay for initial charge attack (in ticks).")]
		public readonly int InitialChargeDelay = 22;
		[Desc("Delay between charge attacks (in ticks).")]
		public readonly int ChargeDelay = 3;
		[Desc("Sound to play when actor charges.")]
		public readonly string ChargeAudio = null;
		*/
		[Desc("Max hops the supporters can reach.")]
		public readonly int MaxHops = 3;

		[Desc("Ticks each hop will take for each hop of support weapon to jump across support network")]
		public readonly int TicksPerHop = 1;

		[Desc("Max support an attacker can get.")]
		public readonly int MaxSupportersPerAttacker = 9;

		[Desc("Can support allies too? Instead of just owners?")]
		public readonly bool CanSupportAllies = false;

		[Desc("When supporting, what armament will this actor use?")]
		public readonly string SupportArmament = "support";

		[Desc("Only actors with this trait and the matching SupportType can buff each other.")]
		public readonly string SupportType = "prism";

		[Desc("When receiving the support, where in this actor will be the hit location?")]
		public readonly WVec ReceiverOffset = WVec.Zero;

		[GrantedConditionReference]
		[Desc("Condition stack to grant upon receiving the buffs.")]
		public readonly string BuffCondition = "prism-stack";

		[Desc("Percentage modifier to apply for each buff.")]
		public readonly int Modifier = 100;

		[GrantedConditionReference]
		[Desc("The condition to grant to self while the charge level is greater than zero.")]
		public readonly string ChargingCondition = null;

		public override object Create(ActorInitializer init) { return new AttackPrismSupportedCA(init.Self, this); }
	}

	public class AttackPrismSupportedCA : AttackPrism, ITick, INotifyAttack, INotifyBecomingIdle, IFirepowerModifier
	{
		readonly AttackPrismSupportedCAInfo info;
		readonly Stack<int> buffTokens = new Stack<int>();
		int chargingToken = Actor.InvalidConditionToken;

		int IFirepowerModifier.GetFirepowerModifier(string armamentName) { return IsTraitDisabled ? 100 : 100 + buffTokens.Count * info.Modifier ; }

		public AttackPrismSupportedCA(Actor self, AttackPrismSupportedCAInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		bool IsValidStance(Actor self, Actor otherNode)
		{
			if (self.Owner == otherNode.Owner)
				return true;

			// It might be interesting if neutral actors support everybody hmmm...
			return info.CanSupportAllies && self.Owner.IsAlliedWith(otherNode.Owner);
		}

		// Check if self may support the receiver,
		// where the receiver may be the one getting buffed or a relay node.
		// We do all but range check here.
		public bool MaySupport(Actor self, Actor receiver, bool selfMustBeIdle)
		{
			if (receiver.IsDead || !receiver.IsInWorld)
				return false;

			if (!IsValidStance(self, receiver))
				return false;

			// I'm busy trying to do something else!
			if (selfMustBeIdle && self.CurrentActivity != null)
				return false;

			// Check traits
			return info.SupportType == receiver.Trait<AttackPrismSupportedCA>().info.SupportType;
		}

		void ITick.Tick(Actor self)
		{
			if (--timeToRecharge <= 0)
				charges = info.MaxCharges;
		}

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			--charges;
			timeToRecharge = info.ReloadDelay;
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		public override Activity GetAttackActivity(Actor self, AttackSource source, in Target newTarget, bool allowMove, bool forceAttack, Color? targetLineColor = null)
		{
			return new ChargeSupportedAttack(this, newTarget, forceAttack, info, targetLineColor);
		}

		public virtual void FireSupportArmament(Actor self, in Target target, Actor buffReceiver)
		{
			if (!CanAttack(self, target))
				return;

			var receiverTrait = buffReceiver.Trait<AttackPrismSupportedCA>();
			var offsetedTarget = Target.FromPos(target.CenterPosition + receiverTrait.info.ReceiverOffset);

			var supportArmament = self.TraitsImplementing<Armament>().First(a => a.Info.Name == info.SupportArmament);

			if (supportArmament.CheckFire(self, facing, offsetedTarget))
				receiverTrait.AddBuffStack(buffReceiver);
		}

		// Check if self can reach target with the support armament.
		public bool CheckSupportRange(Actor self, Actor target)
		{
			var t = Target.FromActor(target);
			var supportArmament = self.TraitsImplementing<Armament>().First(a => a.Info.Name == info.SupportArmament);
			return t.IsInRange(self.CenterPosition, supportArmament.MaxRange());
		}

		void AddBuffStack(Actor self)
		{
			buffTokens.Push(self.GrantCondition(info.BuffCondition));
		}

		void ClearBuffStack(Actor self)
		{
			while (buffTokens.Count > 0)
				self.RevokeCondition(buffTokens.Pop());
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			ClearBuffStack(self);

			if (chargingToken != Actor.InvalidConditionToken)
				chargingToken = self.RevokeCondition(chargingToken);
		}

		class ChargeSupportedAttack : Activity
		{
			readonly AttackPrismSupportedCA attack;
			readonly Target target;
			readonly bool forceAttack;
			readonly Color? targetLineColor;
			readonly AttackPrismSupportedCAInfo supportInfo;

			public ChargeSupportedAttack(
				AttackPrismSupportedCA attack,
				in Target target,
				bool forceAttack,
				AttackPrismSupportedCAInfo supportInfo,
				Color? targetLineColor = null)
			{
				this.attack = attack;
				this.target = target;
				this.forceAttack = forceAttack;
				this.supportInfo = supportInfo;
				this.targetLineColor = targetLineColor;
			}

			// Run BFS rooted at self, to get multi-hop supporters.
			// Returns (supporter, relay, hops) triplets
			public IEnumerable<(Actor, Actor, int)> RecruitSupporters(Actor self)
			{
				var candidates = self.World.ActorsHavingTrait<AttackPrismSupportedCA>();
				var isVisited = new HashSet<Actor>() { self };
				var hops = new Dictionary<Actor, int>() { { self, 0 } };
				var parent = new Dictionary<Actor, Actor>() { { self, null } };
				var queue = new Queue<Actor>();
				var maxHops = 0;

				queue.Enqueue(self);
				while (queue.Count() > 0)
				{
					var node = queue.Dequeue();
					foreach (var adjacent in GetValidNeighborSupporters(node, candidates))
					{
						if (isVisited.Contains(adjacent))
							continue;

						isVisited.Add(adjacent);
						hops[adjacent] = hops[node] + 1;
						parent[adjacent] = node;

						if (maxHops < hops[adjacent])
							maxHops = hops[adjacent];

						if (isVisited.Count() > supportInfo.MaxSupportersPerAttacker)
						{
							queue.Clear(); // terminate the search
							break;
						}

						if (hops[adjacent] < supportInfo.MaxHops)
							queue.Enqueue(adjacent);
					}
				}

				foreach (var node in isVisited)
				{
					if (node == self)
						continue;

					yield return (node, parent[node], hops[node]);
				}
			}

			public IEnumerable<Actor> GetValidNeighborSupporters(Actor self, IEnumerable<Actor> traitedActors)
			{
				// My guess is that is is more common to have more actors within our range
				// than actors with this trait in the world.
				foreach (var cand in traitedActors)
				{
					if (cand == self)
						continue;

					var candAttack = cand.Trait<AttackPrismSupportedCA>();

					if (!candAttack.MaySupport(cand, self, true))
						continue;

					// Implemented this way as the support may have different armament than self.
					if (candAttack.CheckSupportRange(cand, self))
						yield return cand;
				}
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (attack.charges == 0)
					return false;

				foreach (var notify in self.TraitsImplementing<INotifyPrismCharging>())
					notify.Charging(self, target);

				if (attack.chargingToken == Actor.InvalidConditionToken)
					attack.chargingToken = self.GrantCondition(attack.info.ChargingCondition);

				if (!string.IsNullOrEmpty(attack.info.ChargeAudio))
					Game.Sound.Play(SoundType.World, attack.info.ChargeAudio, self.CenterPosition);

				var relays = RecruitSupporters(self); // You need to recruit every time you fire as the battlefield is a very dynamic place.
				var maxHops = relays.Any() ? relays.MaxBy(x => x.Item3).Item3 : 0;

				if (relays.Any())
				{
					foreach ((var supporter, var relay, var hop) in relays)
					{
						var attack = supporter.Trait<AttackPrismSupportedCA>();
						if (!attack.MaySupport(supporter, relay, true))
							continue;

						supporter.QueueActivity(new FireSupportingWeapon(attack, Target.FromActor(relay), self, (maxHops - hop) * attack.info.TicksPerHop, Color.OrangeRed));
					}
				}

				QueueChild(new Wait(attack.info.InitialChargeDelay + maxHops * attack.info.TicksPerHop));
				QueueChild(new ChargeFireBuffed(attack, target));
				return false;
			}

			public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
			{
				if (targetLineColor != null)
					yield return new TargetLineNode(target, targetLineColor.Value);
			}
		}

		class ChargeAndFireSupportWeapon : Activity
		{
			readonly AttackPrismSupportedCA attack;
			readonly Target target;
			readonly Actor buffReceiver;

			public ChargeAndFireSupportWeapon(AttackPrismSupportedCA attack, in Target target, Actor buffReceiver)
			{
				this.attack = attack;
				this.target = target;
				this.buffReceiver = buffReceiver;
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (attack.charges == 0)
					return true;

				if (!attack.MaySupport(self, buffReceiver, false))
					return true;

				if (buffReceiver.IsIdle)
					return true;

				attack.FireSupportArmament(self, target, buffReceiver);
				return false;
			}
		}

		class FireSupportingWeapon : Activity
		{
			readonly AttackPrismSupportedCA attack;
			readonly Target target;
			readonly Actor buffReceiver; // buff receiver is NOT target, as the buff may travel multiple hops.
			readonly int hopDelay;
			readonly Color? targetLineColor;

			public FireSupportingWeapon(AttackPrismSupportedCA attack, Target target, Actor buffReceiver, int hopDelay, Color? targetLineColor = null)
			{
				this.attack = attack;
				this.target = target;
				this.buffReceiver = buffReceiver;
				this.hopDelay = hopDelay;
				this.targetLineColor = targetLineColor;
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (buffReceiver.IsIdle)
					return true;

				if (attack.charges == 0)
					return false;

				foreach (var notify in self.TraitsImplementing<INotifyPrismCharging>())
					notify.Charging(self, target);

				if (!string.IsNullOrEmpty(attack.info.ChargeAudio))
					Game.Sound.Play(SoundType.World, attack.info.ChargeAudio, self.CenterPosition);

				QueueChild(new Wait(attack.info.InitialChargeDelay + hopDelay));
				QueueChild(new ChargeAndFireSupportWeapon(attack, target, buffReceiver));
				return true; // Works only once
			}

			public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
			{
				if (targetLineColor != null)
					yield return new TargetLineNode(target, targetLineColor.Value);
			}
		}

		protected class ChargeFireBuffed : Activity
		{
			readonly AttackPrismSupportedCA attack;
			readonly Target target;

			public ChargeFireBuffed(AttackPrismSupportedCA attack, in Target target)
			{
				this.attack = attack;
				this.target = target;
			}

			public override bool Tick(Actor self)
			{
				if (IsCanceling || !attack.CanAttack(self, target))
					return true;

				if (attack.charges == 0)
				{
					return true;
				}

				attack.DoAttack(self, target);
				attack.ClearBuffStack(self);

				if (attack.chargingToken != Actor.InvalidConditionToken)
					attack.chargingToken = self.RevokeCondition(attack.chargingToken);

				QueueChild(new Wait(attack.info.ChargeDelay));
				return false;
			}
		}
	}
}
