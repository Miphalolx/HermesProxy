using Framework;
using Framework.Constants;
using HermesProxy.Enums;
using System.Collections.Generic;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Threading;
using Framework.Logging;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        SpellCastTargetFlags ConvertSpellTargetFlags(SpellTargetData target)
        {
            SpellCastTargetFlags targetFlags = SpellCastTargetFlags.None;
            if (target.Unit != null && !target.Unit.IsEmpty())
            {
                if (target.Flags.HasFlag(SpellCastTargetFlags.Unit))
                    targetFlags |= SpellCastTargetFlags.Unit;
                if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseEnemy))
                    targetFlags |= SpellCastTargetFlags.CorpseEnemy;
                if (target.Flags.HasFlag(SpellCastTargetFlags.GameObject))
                    targetFlags |= SpellCastTargetFlags.GameObject;
                if (target.Flags.HasFlag(SpellCastTargetFlags.CorpseAlly))
                    targetFlags |= SpellCastTargetFlags.CorpseAlly;
                if (target.Flags.HasFlag(SpellCastTargetFlags.UnitMinipet))
                    targetFlags |= SpellCastTargetFlags.UnitMinipet;
            }
            if (target.Item != null & !target.Item.IsEmpty())
            {
                if (target.Flags.HasFlag(SpellCastTargetFlags.Item))
                    targetFlags |= SpellCastTargetFlags.Item;
                if (target.Flags.HasFlag(SpellCastTargetFlags.TradeItem))
                    targetFlags |= SpellCastTargetFlags.TradeItem;
            }
            if (target.SrcLocation != null)
                targetFlags |= SpellCastTargetFlags.SourceLocation;
            if (target.DstLocation != null)
                targetFlags |= SpellCastTargetFlags.DestLocation;
            if (!String.IsNullOrEmpty(target.Name))
                targetFlags |= SpellCastTargetFlags.String;
            return targetFlags;
        }
        void WriteSpellTargets(SpellTargetData target, SpellCastTargetFlags targetFlags, WorldPacket packet)
        {
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                packet.WriteUInt16((ushort)targetFlags);
            else
                packet.WriteUInt32((uint)targetFlags);

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Unit | SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.GameObject |
                SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.UnitMinipet))
                packet.WritePackedGuid(target.Unit.To64());

            // Check if the user wants to target the "Will not be traded" slot
            if (targetFlags.HasFlag(SpellCastTargetFlags.TradeItem) && target.Item == WowGuid128.Create(HighGuidType703.Uniq, 10))
                packet.WritePackedGuid(new WowGuid64((ulong) TradeSlots.NonTraded));
            else if (targetFlags.HasFlag(SpellCastTargetFlags.Item))
                packet.WritePackedGuid(target.Item.To64());

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
            {
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                    packet.WritePackedGuid(target.SrcLocation.Transport.To64());
                packet.WriteVector3(target.SrcLocation.Location);
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
            {
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                    packet.WritePackedGuid(target.DstLocation.Transport.To64());
                packet.WriteVector3(target.DstLocation.Location);
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
                packet.WriteCString(target.Name);
        }
        public void SendCastRequestFailed(ClientCastRequest castRequest, bool isPet)
        {
            if (!castRequest.HasStarted)
            {
                SpellPrepare prepare2 = new SpellPrepare();
                prepare2.ClientCastID = castRequest.ClientGUID;
                prepare2.ServerCastID = castRequest.ServerGUID;
                SendPacket(prepare2);
            }

            if (isPet)
            {
                PetCastFailed failed = new();
                failed.SpellID = castRequest.SpellId;
                failed.Reason = (uint)SpellCastResultClassic.SpellInProgress;
                failed.CastID = castRequest.ServerGUID;
                SendPacket(failed);
            }
            else
            {
                CastFailed failed = new();
                failed.SpellID = castRequest.SpellId;
                failed.SpellXSpellVisualID = castRequest.SpellXSpellVisualId;
                failed.Reason = (uint)SpellCastResultClassic.SpellInProgress;
                failed.CastID = castRequest.ServerGUID;
                SendPacket(failed);
            }    
        }



        [PacketHandler(Opcode.CMSG_CAST_SPELL)]
        void HandleCastSpell(CastSpell cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0 && cast.Cast.SpellID!=5384 && cast.Cast.SpellID!= 20904  && cast.Cast.SpellID!= 14290 && cast.Cast.SpellID!= 2643 &&cast.Cast.SpellID!=19503)
                Thread.Sleep(Settings.ServerSpellDelay);

            if (GameData.NextMeleeSpells.Contains(cast.Cast.SpellID) ||
                GameData.AutoRepeatSpells.Contains(cast.Cast.SpellID))
            {
                ClientCastRequest castRequest = new ClientCastRequest();
                castRequest.Timestamp = Environment.TickCount;
                castRequest.SpellId = cast.Cast.SpellID;
                castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
                castRequest.ClientGUID = cast.Cast.CastID;
                Log.Print(LogType.Warn, $"Auto shoot spell fired.");
                if (GetSession().GameState.CurrentClientSpecialCast != null && GetSession().GameState.CurrentClientSpecialCast.SpellId == cast.Cast.SpellID)
                {
                    if(GameData.AutoRepeatSpells.Contains(cast.Cast.SpellID)) {
                    castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());
                    SendCastRequestFailed(castRequest, false);
                    return;
                    }
                }
                else
                {
                    lock(GameData.LastSpecialSpellTime){
                    long spellTime = DateTime.Now.Ticks;
                        if(!GameData.LastSpecialSpellTime.ContainsKey(castRequest.SpellId)){
                            GameData.LastSpecialSpellTime.Add(castRequest.SpellId,DateTime.Now.Ticks);
                        } else {
                        if((spellTime - GameData.LastSpecialSpellTime[castRequest.SpellId])/10000<50){
                            Log.Print(LogType.Warn, $"Last Special spell too short{castRequest.SpellId} {(spellTime - GameData.LastSpecialSpellTime[castRequest.SpellId])/10000} ");
                            SendCastRequestFailed(castRequest, false);
                            return;
                        } else {
                            GameData.LastSpecialSpellTime[castRequest.SpellId]=spellTime;
                        }
                        }                        
                    }
                    
                    GetSession().GameState.CurrentClientSpecialCast = castRequest;
                    castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, cast.Cast.SpellID + GetSession().GameState.CurrentPlayerGuid.GetCounter());
                    SpellPrepare prepare = new SpellPrepare();
                    prepare.ClientCastID = cast.Cast.CastID;
                    prepare.ServerCastID = castRequest.ServerGUID;
                    SendPacket(prepare);
                  
                } 
            }
            else
            {
                ClientCastRequest castRequest = new ClientCastRequest();
                castRequest.Timestamp = Environment.TickCount;
                castRequest.SpellId = cast.Cast.SpellID;
                castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
                castRequest.ClientGUID = cast.Cast.CastID;
                castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

                // prevent the client spell to much.
                lock(GameData.LastSpellTime){
                    long spellTime = DateTime.Now.Ticks;
                    if(!GameData.LastSpellTime.ContainsKey(castRequest.SpellId)){
                        GameData.LastSpellTime.Add(castRequest.SpellId,DateTime.Now.Ticks);
                    } else {
                       int interval = 100;
                       if(castRequest.SpellId==14290) {
                         interval = 500;
                       }
                       if((spellTime - GameData.LastSpellTime[castRequest.SpellId])/10000<interval){
                        Log.Print(LogType.Warn, $"Last spell too short{castRequest.SpellId} {(spellTime - GameData.LastSpellTime[castRequest.SpellId])/10000} ");
                        SendCastRequestFailed(castRequest, false);
                        return;
                       } else {
                         GameData.LastSpellTime[castRequest.SpellId]=spellTime;
                       }
                    }                        
                }

                ClientCastRequest CurrentClientNormalCast = null;

                lock(GetSession().GameState.CurrentClientNormalCastQueue){
                    if(GetSession().GameState.CurrentClientNormalCastQueue.Count != 0){
                        CurrentClientNormalCast = GetSession().GameState.CurrentClientNormalCastQueue.Peek();
                    }
                }

                if (CurrentClientNormalCast != null)
                {
                    
                    Log.Print(LogType.Warn, $"CurrentClientNormalCast not null {castRequest.SpellId} ");

                        if(CurrentClientNormalCast.SpellId  == castRequest.SpellId ) {
                            Log.Print(LogType.Warn, $"同类法术已成功施法{castRequest.SpellId }忽略.");
                            SendCastRequestFailed(castRequest, false);
                            return;
                        }

                        if(CurrentClientNormalCast.ItemGUID != null) {
                            Thread.Sleep(Settings.MacroSpellDelay);
                        } 

                        // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                        if (CurrentClientNormalCast.Timestamp + 10000 < castRequest.Timestamp)
                        {
                            Log.Print(LogType.Warn, $"Clearing CurrentClientNormalCast because of 10 sec timeout! (oldSpell:{CurrentClientNormalCast.SpellId} newSpell:{castRequest.SpellId})");
                            Log.Print(LogType.Warn, "Are you playing on a server with another patch?");
                            SendCastRequestFailed(CurrentClientNormalCast, false);
                            lock(GetSession().GameState.CurrentClientNormalCastQueue){ 
                                try{
                                GetSession().GameState.CurrentClientNormalCastQueue.Dequeue();
                                }catch(Exception e){}
                            }
                            SendCastRequestFailed(castRequest, false);
                        }
                }

                lock(GetSession().GameState.CurrentClientNormalCastQueue){
                    GetSession().GameState.CurrentClientNormalCastQueue.Enqueue(castRequest);
                }
            }

            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CAST_SPELL);
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                packet.WriteUInt32(cast.Cast.SpellID);
            }
            else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                packet.WriteUInt32(cast.Cast.SpellID);
                packet.WriteUInt8(0); // cast count
            }
            else
            {
                packet.WriteUInt8(0); // cast count
                packet.WriteUInt32(cast.Cast.SpellID);
                packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
            }
            WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
            
            SendPacketToServer(packet);
            // force cancel combat 
            if(cast.Cast.SpellID == 5384) {
                Thread.Sleep(50);
                CancelCombat combat = new();
                SendPacket(combat);
            }
        }
        [PacketHandler(Opcode.CMSG_PET_CAST_SPELL)]
        void HandlePetCastSpell(PetCastSpell cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = cast.Cast.SpellID;
            castRequest.SpellXSpellVisualId = cast.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = cast.Cast.CastID;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, cast.Cast.SpellID, 10000 + cast.Cast.CastID.GetCounter());

            if (GetSession().GameState.CurrentClientPetCast != null)
            {
                if (GetSession().GameState.CurrentClientPetCast.HasStarted)
                {
                    SendCastRequestFailed(castRequest, true);
                }
                else
                {
                    // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                    if (GetSession().GameState.CurrentClientPetCast.Timestamp + 10000 < castRequest.Timestamp)
                    {
                        Log.Print(LogType.Warn, $"Clearing CurrentClientPetCast because of 10 sec timeout! (oldSpell:{GetSession().GameState.CurrentClientPetCast.SpellId} newSpell:{castRequest.SpellId})");
                        SendCastRequestFailed(GetSession().GameState.CurrentClientPetCast, true);
                        GetSession().GameState.CurrentClientPetCast = null;
                        foreach (var pending in GetSession().GameState.PendingClientPetCasts)
                            SendCastRequestFailed(pending, true);
                        GetSession().GameState.PendingClientPetCasts.Clear();
                        SendCastRequestFailed(castRequest, true);
                    }
                    else
                        GetSession().GameState.PendingClientPetCasts.Add(castRequest);
                }
                return;
            }

            GetSession().GameState.CurrentClientPetCast = castRequest;

            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(cast.Cast.Target);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_PET_CAST_SPELL);
            packet.WriteGuid(cast.PetGUID.To64());
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8(0); // cast count
            packet.WriteUInt32(cast.Cast.SpellID);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8((byte)cast.Cast.SendCastFlags);
            WriteSpellTargets(cast.Cast.Target, targetFlags, packet);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_USE_ITEM)]
        void HandleUseItem(UseItem use)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            ClientCastRequest castRequest = new ClientCastRequest();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = use.Cast.SpellID;
            castRequest.SpellXSpellVisualId = use.Cast.SpellXSpellVisualID;
            castRequest.ClientGUID = use.Cast.CastID;
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, use.Cast.SpellID, 10000 + use.Cast.CastID.GetCounter());
            castRequest.ItemGUID = use.CastItem;

            ClientCastRequest CurrentClientNormalCast = null;

             if (GameData.MountAuras.Contains(use.Cast.SpellID))
             {
               if(GameData.StealthStatus == true) {
                    CooldownEvent cooldown = new();
                    cooldown.SpellID = 1787;
                    Log.Print(LogType.Warn, $"Manual force reset StealthStatus");
                    GameData.StealthStatus = false;
                    SendPacket(cooldown);
                }
             }
          

                lock(GetSession().GameState.CurrentClientNormalCastQueue){
                    if(GetSession().GameState.CurrentClientNormalCastQueue.Count != 0){
                        CurrentClientNormalCast = GetSession().GameState.CurrentClientNormalCastQueue.Peek();
                    }
                }

                // prevent the client spell to much.
                lock(GameData.LastSpellTime){
                    long spellTime = DateTime.Now.Ticks;
                    if(!GameData.LastSpellTime.ContainsKey(castRequest.SpellId)){
                        GameData.LastSpellTime.Add(castRequest.SpellId,DateTime.Now.Ticks);
                    } else {
                       if((spellTime - GameData.LastSpellTime[castRequest.SpellId])/10000<100){
                        Log.Print(LogType.Warn, $"Last spell too short{castRequest.SpellId} {(spellTime - GameData.LastSpellTime[castRequest.SpellId])/10000} ");
                        SendCastRequestFailed(castRequest, false);
                        return;
                       } else {
                         GameData.LastSpellTime[castRequest.SpellId]=spellTime;
                       }
                    }                        
                }

            if (CurrentClientNormalCast!= null)
            {  try{

                if (CurrentClientNormalCast.HasStarted)
                {
                    SendCastRequestFailed(castRequest, false);
                    return;
                } else {
                    // Sometimes we dont clear the CurrentCast when we dont get the correct SMSG_SPELL_GO
                    if (CurrentClientNormalCast.Timestamp + 10000 < castRequest.Timestamp)
                    {
                        Log.Print(LogType.Warn, $"Clearing CurrentClientNormalCast because of 10 sec timeout! (oldSpell:{CurrentClientNormalCast.SpellId} newSpell:{castRequest.SpellId})");
                        
                        lock(GetSession().GameState.CurrentClientNormalCastQueue){
                            foreach(ClientCastRequest r in GetSession().GameState.CurrentClientNormalCastQueue){
                                SendCastRequestFailed(r, false);
                            }
                            GetSession().GameState.CurrentClientNormalCastQueue.Clear();        
                        }
                    }
                    
                    if(CurrentClientNormalCast.ItemGUID != null) {
                        Log.Print(LogType.Warn, $"Item using oldSpell:{CurrentClientNormalCast.SpellId} newSpell:{castRequest.SpellId})");
                        Thread.Sleep(Settings.MacroSpellDelay);
                    }

                    lock(GetSession().GameState.CurrentClientNormalCastQueue){
                    if(GetSession().GameState.CurrentClientNormalCastQueue.Count != 0){
                        foreach(ClientCastRequest r in GetSession().GameState.CurrentClientNormalCastQueue){
                            if(r.SpellId == castRequest.SpellId) {
                                Log.Print(LogType.Warn, $"Same Item using ignore  :{GetSession().GameState.CurrentClientNormalCastQueue.Count}");
                                return;
                            }
                        }
                    }
                }
                }
                }catch(Exception e){}
            }

             lock(GetSession().GameState.CurrentClientNormalCastQueue){ 
                GetSession().GameState.CurrentClientNormalCastQueue.Enqueue(castRequest);
            }

            WorldPacket packet = new WorldPacket(Opcode.CMSG_USE_ITEM);
            byte containerSlot = use.PackSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.PackSlot) : use.PackSlot;
            byte slot = use.PackSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(use.Slot) : use.Slot;
            packet.WriteUInt8(containerSlot);
            packet.WriteUInt8(slot);
            packet.WriteUInt8(GetSession().GameState.GetItemSpellSlot(use.CastItem, use.Cast.SpellID));
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                packet.WriteUInt8(0); // cast count;
                packet.WriteGuid(use.CastItem.To64());
            }
            SpellCastTargetFlags targetFlags = ConvertSpellTargetFlags(use.Cast.Target);
            WriteSpellTargets(use.Cast.Target, targetFlags, packet);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_CAST)]
        void HandleCancelCast(CancelCast cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CAST);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                packet.WriteUInt8(0);
            packet.WriteUInt32(cast.SpellID);
            try{
             if (GetSession().GameState.CurrentClientSpecialCast != null &&
                GetSession().GameState.CurrentClientSpecialCast.SpellId == cast.SpellID)
            {
                GetSession().GameState.CurrentClientSpecialCast = null;
            } else if (GetSession().GameState.CurrentClientNormalCastQueue.Count>0)
            {
                lock(GetSession().GameState.CurrentClientNormalCastQueue){
                    List<ClientCastRequest> oldrequests = new List<ClientCastRequest>();
                        foreach(ClientCastRequest r in GetSession().GameState.CurrentClientNormalCastQueue){
                            bool found = false;
                            if(r.SpellId != cast.SpellID||found) {
                                oldrequests.Add(r);
                                found = true;
                            } else {
                             found = true;
                            }
                        }
                        GetSession().GameState.CurrentClientNormalCastQueue.Clear();
                        foreach(ClientCastRequest r in oldrequests){
                            GetSession().GameState.CurrentClientNormalCastQueue.Enqueue(r);
                        }              
                }
            }
            SendPacketToServer(packet);
            }catch{}
        }
        [PacketHandler(Opcode.CMSG_CANCEL_CHANNELLING)]
        void HandleCancelChannelling(CancelChannelling cast)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_CHANNELLING);
            packet.WriteInt32(cast.SpellID);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL)]
        void HandleCancelAutoRepeatSpell(CancelAutoRepeatSpell spell)
        {
            // Artificial lag is needed for spell packets,
            // or spells will bug out and glow if spammed.
            if (Settings.ServerSpellDelay > 0)
                Thread.Sleep(Settings.ServerSpellDelay);

            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AUTO_REPEAT_SPELL);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_AURA)]
        void HandleCancelAura(CancelAura aura)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
            packet.WriteUInt32(aura.SpellID);
            // 潜行
            if(aura.SpellID ==1787 && GameData.StealthStatus == true) {
                CooldownEvent cooldown = new();
                cooldown.SpellID = aura.SpellID;
                Log.Print(LogType.Warn, $"Manual force reset StealthStatus");
                SendPacket(cooldown);
            }
            // 冷血
            if(aura.SpellID ==14177 && GameData.ColdBloodStatus == true) {
                CooldownEvent cooldown = new();
                cooldown.SpellID = aura.SpellID;
                Log.Print(LogType.Warn, $"Manual force reset StealthStatus");
                SendPacket(cooldown);
            }

            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_CANCEL_MOUNT_AURA)]
        void HandleCancelMountAura(EmptyClientPacket cancel)
        {
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_MOUNT_AURA);
                SendPacketToServer(packet);
            }
            else
            {
                WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
                var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(guid);
                if (updateFields == null)
                    return;

                for (byte i = 0; i < 32; i++)
                {
                    var aura = GetSession().WorldClient.ReadAuraSlot(i, guid, updateFields);
                    if (aura == null)
                        continue;

                    if (GameData.MountAuras.Contains(aura.SpellID))
                    {
                        WorldPacket packet = new WorldPacket(Opcode.CMSG_CANCEL_AURA);
                        packet.WriteUInt32(aura.SpellID);
                        SendPacketToServer(packet);
                    }
                }
            }
        }
        [PacketHandler(Opcode.CMSG_LEARN_TALENT)]
        void HandleLearnTalent(LearnTalent talent)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_LEARN_TALENT);
            packet.WriteUInt32(talent.TalentID);
            packet.WriteUInt32(talent.Rank);
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_RESURRECT_RESPONSE)]
        void HandleResurrectResponse(ResurrectResponse revive)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_RESURRECT_RESPONSE);
            packet.WriteGuid(revive.CasterGUID.To64());
            packet.WriteUInt8((byte)(revive.Response != 0 ? 0 : 1));
            SendPacketToServer(packet);
        }
        [PacketHandler(Opcode.CMSG_SELF_RES)]
        void HandleSelfRes(SelfRes revive)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SELF_RES);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_TOTEM_DESTROYED)]
        void HandleTotemDestroyed(TotemDestroyed totem)
        {
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                return;

            WorldPacket packet = new WorldPacket(Opcode.CMSG_TOTEM_DESTROYED);
            packet.WriteUInt8(totem.Slot);
            SendPacketToServer(packet);
        }
    }
}
