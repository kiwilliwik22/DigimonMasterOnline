using System.Resources;
using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.TamerShop;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.PersonalShop;
using DigitalWorldOnline.Infraestructure.ContextConfiguration.Shop;
using MediatR;
using Serilog;
using System.Threading;
using DigitalWorldOnline.GameHost;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ConsignedShopPurchaseItemPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ConsignedShopPurchaseItem;

        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly IMapper _mapper;
        private readonly ISender _sender;
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1); // SemaphoreSlim untuk mengatasi CS1996

        public ConsignedShopPurchaseItemPacketProcessor(
            AssetsLoader assets,
            MapServer mapServer,
            ILogger logger,
            IMapper mapper,
            ISender sender)
        {
            _assets = assets;
            _mapServer = mapServer;
            _logger = logger;
            _mapper = mapper;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            _logger.Information($"Getting parameters...");
            var shopHandler = packet.ReadInt();
            var shopSlot = packet.ReadInt();
            var boughtItemId = packet.ReadInt();
            var boughtAmount = packet.ReadInt();
            packet.Skip(60);
            var boughtUnitPrice = packet.ReadInt64();

            _logger.Information($"{shopHandler} {shopSlot} {boughtItemId} {boughtAmount} {boughtUnitPrice}");

            _logger.Information($"Searching consigned shop {shopHandler}...");
            var shop = _mapper.Map<ConsignedShop>(await _sender.Send(new ConsignedShopByHandlerQuery(shopHandler)));
            if (shop == null)
            {
                _logger.Information($"Consigned shop {shopHandler} not found...");
                client.Send(new UnloadConsignedShopPacket(shopHandler));
                return;
            }

            if (client.TamerId == shop.CharacterId) // assuming client.TamerId is the shop owner's ID
            {
                _logger.Error("Player attempted to buy from their own shop.");
                return;
            }

            var seller =
                _mapper.Map<CharacterModel>(await _sender.Send(new CharacterAndItemsByIdQuery(shop.CharacterId)));
            if (seller == null)
            {
                _logger.Information($"Deleting consigned shop {shopHandler}...");
                await _sender.Send(new DeleteConsignedShopCommand(shopHandler));

                _logger.Information($"Consigned shop owner {shop.CharacterId} not found...");
                client.Send(new UnloadConsignedShopPacket(shopHandler));
                return;
            }

            var totalValue = boughtUnitPrice * boughtAmount;
            var newItem = new ItemModel(boughtItemId, boughtAmount); // Inisialisasi newItem di sini
            if (newItem != null)
            {
                // Cek ketersediaan item
                _logger.Information($"Removing {totalValue} bits...");
                client.Tamer.Inventory.RemoveBits(totalValue);

                _logger.Information($"Updating inventory bits...");
                await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory));

                newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == boughtItemId));

                _logger.Information($"Adding bought item...");
                client.Tamer.Inventory.AddItems(((ItemModel)newItem.Clone()).GetList());

                _logger.Information($"Updating item list...");
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                //client.Send(new ConsignedShopItemsViewPacket());
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                // Update Shop

                _logger.Information($"Removing consigned shop bought item...");
                //newItem.ReduceAmount(boughtAmount);
                seller.ConsignedShopItems.RemoveOrReduceItems(newItem.GetList());
                _logger.Information($"Updating consigned shop item list...");
                // After updating the seller's consigned shop items
                await _sender.Send(new UpdateItemsCommand(seller.ConsignedShopItems.Items));

                // Broadcast the updated items to the seller and other clients
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new ConsignedShopItemsViewPacket(shop, seller.ConsignedShopItems, seller.Name).Serialize());

                // Log the update for debugging
                _logger.Information($"Updated seller's consigned shop items for seller ID: {seller.Id}");                
                await _sender.Send(new ConsignedShopByHandlerQuery(shopHandler));

                client.Send(
                    new SystemMessagePacket($"Successfully bought {boughtItemId} x{boughtAmount} from Shop {shop}."));
                _logger.Information($"Removing consigned shop bought item...");
                newItem.SetItemId();
                var allItemsRemoved = true;
                var remainingAmount = boughtAmount;
                while (newItem.Amount > 0)
                {
                    seller.ConsignedShopItems.RemoveOrReduceItem(newItem, newItem.Amount);
                    if (!seller.ConsignedShopItems.RemoveOrReduceItem(newItem, remainingAmount))
                    {
                        var anyRemoved = false;
                        for (var i = remainingAmount; i > 0; i--)
                            if (seller.ConsignedShopItems.RemoveOrReduceItem(newItem, 1))
                            {
                                remainingAmount--;
                                anyRemoved = true;
                                break;
                            }

                        if (!anyRemoved)
                        {
                            seller.ConsignedShopItems.CheckEmptyItems();
                            return;
                        }
                    }
                    else
                    {
                        remainingAmount = 0;
                    }
                }
                if (!allItemsRemoved)
                {
                    // Handle the case where not all items could be removed
                    // For example, send a message to the client
                    client.Send(new SystemMessagePacket("Not all requested items could be removed from the shop."));
                }
            }

            //var sellerClient = client.Server.FindByTamerId(shop.CharacterId);
            //if (sellerClient != null && sellerClient.IsConnected)
            //{
            //    _logger.Information($"Sending system message packet {sellerClient.TamerId}...");
            //    var itemName = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == boughtItemId)?.Name ?? "item";
            //    var message = $"You have sold x{boughtAmount} {itemName} in Consigned Store!";
            //    sellerClient.Send(new SystemMessagePacket(message));

            //    _logger.Information($"Adding {totalValue} bits to {sellerClient.TamerId} consigned warehouse...");
            //    sellerClient.Tamer.ConsignedWarehouse.AddBits(totalValue);

            //    _logger.Information($"Updating {sellerClient.TamerId} consigned warehouse...");
            //    await _sender.Send(new UpdateItemListBitsCommand(sellerClient.Tamer.ConsignedWarehouse));
            //}

            //if (seller.ConsignedShopItems.Count == 0)
            //{
            //    var sellerItem =
            //        seller.ConsignedShopItems.Items.FirstOrDefault(x =>
            //            x.ItemId == boughtItemId && x.Amount >= boughtAmount);

            //    seller.ConsignedShopItems.RemoveOrReduceItems(sellerItem.GetList());

            //    _logger.Information($"Deleting consigned shop {shopHandler}...");
            //    await _sender.Send(new DeleteConsignedShopCommand(shopHandler));

            //    _logger.Information($"Sending unload consigned shop packet {shopHandler}...");
            //    client.Send(new UnloadConsignedShopPacket(shopHandler));

            //    _logger.Information($"Sending consigned shop close packet...");
            //    sellerClient?.Send(new ConsignedShopClosePacket());
            //}
            //else
            //{
            //    seller.ConsignedShopItems.Items.ForEach(item =>
            //    {
            //        item.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == item.ItemId));
            //    });
            //}
            //// Update the seller's consigned shop items
            ////seller.ConsignedShopItems.RemoveOrReduceItemsByItemId(newItem.ItemId, boughtAmount);
            //// Check if all slots are empty
            //bool allSlotsEmpty = seller.ConsignedShopItems.Items.All(item => item.Amount == 0);

            //// If all slots are empty, close the shop
            //if (allSlotsEmpty)
            //{
            //    _logger.Information($"Deleting consigned shop {shopHandler}...");
            //    await _sender.Send(new DeleteConsignedShopCommand(shopHandler));

            //    _logger.Information($"Sending unload consigned shop packet {shopHandler}...");
            //    client.Send(new UnloadConsignedShopPacket(shopHandler));

            //    _logger.Information($"Sending consigned shop close packet...");
            //    sellerClient?.Send(new ConsignedShopClosePacket());
            //}
            //else
            //{
            //    // Update the shop's item list
            //    seller.ConsignedShopItems.Items.ForEach(item =>
            //    {
            //        item.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == item.ItemId));
            //    });

            //    // Send updated shop view to the client
            //    client.Send(new ConsignedShopItemsViewPacket(shop, seller.ConsignedShopItems, seller.Name));
            //}

            _logger.Information($"Sending load inventory packet...");
            //client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

            _logger.Information($"Sending consigned shop bought item packet...");
            //client.Send(new ConsignedShopBoughtItemPacket(shopSlot, boughtAmount));

            _logger.Information($"Sending consigned shop item list view packet...");
        }
    }
}
