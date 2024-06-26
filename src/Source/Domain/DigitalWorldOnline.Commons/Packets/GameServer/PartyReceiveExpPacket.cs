﻿using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class PartyReceiveExpPacket : PacketWriter
    {
        private const int PacketNumber = 2311;

        public PartyReceiveExpPacket(long tamerExp, long bonusTamerExp,
            long tamerFinalExp, uint partnerHandle, long partnerExp,
            long bonusPartnerExp, long partnerFinalExp, long skillExp,
            string partySourceName)
        {
            Type(PacketNumber);
            WriteInt64(tamerExp * 100);
            WriteInt64(bonusTamerExp * 100);
            WriteInt64(tamerFinalExp * 100);
            WriteUInt(partnerHandle);
            WriteInt64(partnerExp * 100);
            WriteInt64(bonusPartnerExp * 100);
            WriteInt64(partnerFinalExp * 100);
            WriteUInt((uint)(skillExp * 100));
            WriteString(partySourceName);
        }
    }
}