using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.TamerShop;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.PersonalShop;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ConsignedShopOpenPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ConsignedShopOpen;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        public ConsignedShopOpenPacketProcessor(
            MapServer mapServer,
            AssetsLoader assets,
            ILogger logger,
            ISender sender)
        {
            _mapServer = mapServer;
            _assets = assets;
            _logger = logger;
            _sender = sender;
        }
        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            _logger.Information("Getting parameters...");
            var posX = packet.ReadInt();
            var posY = packet.ReadInt();
            packet.Skip(4);
            var shopName = packet.ReadString();
            packet.Skip(9);
            var sellQuantity = packet.ReadInt();
            if (sellQuantity < 0)
            {
                _logger.Error("Invalid sellQuantity received.");
                return;
            }
            List<ItemModel> sellList = new(sellQuantity);
            for (int i = 0; i < sellQuantity; i++)
            {
                var sellItem = new ItemModel(packet.ReadInt(), packet.ReadInt());
                packet.Skip(64);
                sellItem.SetSellPrice(packet.ReadInt());
                packet.Skip(12);
                sellList.Add(sellItem);
            }
            _logger.Information($"{posX} {posY} {shopName} {sellQuantity}");
            try
            {
                foreach (var item in sellList)
                {
                    var itemInfo = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == item.ItemId);
                    if (itemInfo != null && itemInfo.Overlap <= 0)
                    {
                        client.Send(new SystemMessagePacket($"Item {itemInfo.ItemId} is sold."));
                        continue;
                    }
                    item.SetItemInfo(itemInfo);
                    _logger.Information($"{item.ItemId} {item.Amount} {item.TamerShopSellPrice}");
                }
                _logger.Debug("Updating consigned shop item list...");
                client.Tamer.ConsignedShopItems.AddItems(sellList.Clone());
                client.Tamer.Inventory.RemoveOrReduceItems(sellList.Clone());
                await Task.WhenAll(
                    _sender.Send(new UpdateItemsCommand(client.Tamer.ConsignedShopItems)),
                    _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory))
                );
                _logger.Debug("Creating consigned shop...");
                var newShop = ConsignedShop.Create(
                    client.TamerId,
                    shopName,
                    posX,
                    posY,
                    client.Tamer.Location.MapId,
                    client.Tamer.Channel,
                    client.Tamer.ShopItemId
                );
                var result = await _sender.Send(new CreateConsignedShopCommand(newShop));
                if (result != null)
                {
                    newShop.SetId(result.Id);
                    newShop.SetGeneralHandler(result.GeneralHandler);
                    _logger.Debug($"Consigned shop created successfully with ID: {result.Id} and GeneralHandler: {result.GeneralHandler}.");
                    client.Send(new SystemMessagePacket($"Consigned Shop Opened."));
                }
                else
                {
                    _logger.Error("Failed to create consigned shop. The result is null.");
                }
                _logger.Information("Sending consigned shop load packet...");
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new LoadConsignedShopPacket(newShop).Serialize());
                _logger.Information("Sending personal shop close packet...");
                client.Tamer.UpdateShopItemId(0);
                client.Send(new PersonalShopPacket(TamerShopActionEnum.CloseWindow, client.Tamer.ShopItemId));
                client.Tamer.RestorePreviousCondition();
                _logger.Information("Sending sync in condition packet...");
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SyncConditionPacket(client.Tamer.GeneralHandler, client.Tamer.CurrentCondition).Serialize());
            }
            catch (Exception ex)
            {
                _logger.Error($"Error during shop processing: {ex.Message}", ex);
            }
        }
    }
}
