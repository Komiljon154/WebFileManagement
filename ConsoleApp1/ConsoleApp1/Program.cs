using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PaidTelegramBot
{
    public class Program
    {
        private static ITelegramBotClient _botClient;
        private static Dictionary<long, UserState> _userStates = new();
        private static Database _db = new();
        private static BotConfig _config = new()
        {
            MonthlyFee = 50000,
            ReferralBonus = 10000,
            AdminIds = new List<long> { 7386328037 } // O'z admin ID'ingizni qo'ying
        };

        public static async Task Main()
        {
            _botClient = new TelegramBotClient("7849698261:AAGyFK1hDTyKyJM8DLmXZFifywmUOLOW6uw");

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                receiverOptions
            );

            Console.WriteLine($"Bot ishga tushdi: @{await _botClient.GetMeAsync()}");
            await Task.Delay(-1); 
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            try
            {
                switch (update.Type)
                {
                    case UpdateType.Message:
                        await HandleMessage(update.Message);
                        break;
                    case UpdateType.CallbackQuery:
                        await HandleCallbackQuery(update.CallbackQuery);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Xato: {ex.Message}");
            }
        }

        private static async Task HandleMessage(Message message)
        {
            if (message?.From == null) return;

            var chatId = message.Chat.Id;
            var text = message.Text ?? string.Empty;
            var user = _db.GetOrCreateUser(chatId, message.From);

            // Admin komandalarini tekshirish
            if (_config.AdminIds.Contains(chatId))
            {
                if (text.StartsWith("/addchannel"))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Kanal ID sini yuboring:");
                    _userStates[chatId] = UserState.AwaitingChannelId;
                    return;
                }
                else if (text.StartsWith("/setprice"))
                {
                    await _botClient.SendTextMessageAsync(chatId, "Yangi oylik to'lov narxini kiriting:");
                    _userStates[chatId] = UserState.AwaitingNewPrice;
                    return;
                }
            }

            // Foydalanuvchi holatini tekshirish
            if (_userStates.TryGetValue(chatId, out var state))
            {
                await ProcessUserState(chatId, text, state, message);
                return;
            }

            // Asosiy menyu
            switch (text)
            {
                case "/start":
                    await ShowMainMenu(chatId, user);
                    break;
                case "💳 To'lov qilish":
                    await HandlePayment(chatId);
                    break;
                case "💰 Hisobim":
                    await ShowBalance(chatId, user);
                    break;
                case "👤 Kabinet":
                    await ShowUserCabinet(chatId, user);
                    break;
            }
        }

        private static async Task HandleCallbackQuery(CallbackQuery callbackQuery)
        {
            if (callbackQuery?.Message == null) return;

            var chatId = callbackQuery.Message.Chat.Id;
            var data = callbackQuery.Data;

            if (data == "payment_done")
            {
                await ProcessPaymentConfirmation(chatId);
            }
            else if (data.StartsWith("approve_"))
            {
                var userId = long.Parse(data.Split('_')[1]);
                await ApprovePayment(userId, chatId);
            }
            else if (data.StartsWith("edit_"))
            {
                var userId = long.Parse(data.Split('_')[1]);
                _userStates[chatId] = UserState.AwaitingEditAmount;
                _db.SetTempData(chatId, "edit_user", userId.ToString());
                await _botClient.SendTextMessageAsync(chatId, "Yangi to'lov summasini kiriting:");
            }
            else if (data.StartsWith("reject_"))
            {
                var userId = long.Parse(data.Split('_')[1]);
                _userStates[chatId] = UserState.AwaitingRejectionReason;
                _db.SetTempData(chatId, "reject_user", userId.ToString());
                await _botClient.SendTextMessageAsync(chatId, "Rad etish sababini kiriting:");
            }
        }

        #region Foydalanuvchi Interfeysi

        private static async Task ShowMainMenu(long chatId, User user)
        {
            var menu = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("💳 To'lov qilish") },
                new[] { new KeyboardButton("💰 Hisobim"), new KeyboardButton("👤 Kabinet") }
            })
            {
                ResizeKeyboard = true
            };

            await _botClient.SendTextMessageAsync(
                chatId,
                $"Xush kelibsiz, {user.Name}!\nMaxfiy kanalga kirish uchun obuna bo'ling.",
                replyMarkup: menu);
        }

        private static async Task HandlePayment(long chatId)
        {
            _userStates[chatId] = UserState.None;

            var paymentInfo = $"💳 To'lov uchun kartaga pul o'tkazing:\n" +
                             $"Karta raqami: <code>8600 1234 5678 9012</code>\n" +
                             $"💵 Oylik obuna narxi: {_config.MonthlyFee} so'm\n\n" +
                             $"To'lov qilganingizdan so'ng quyidagi tugmani bosing.";

            var inlineKeyboard = new InlineKeyboardMarkup(
                InlineKeyboardButton.WithCallbackData("✅ To'lov qildim", "payment_done"));

            await _botClient.SendTextMessageAsync(
                chatId,
                paymentInfo,
                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard);
        }

        private static async Task ProcessPaymentConfirmation(long chatId)
        {
            _userStates[chatId] = UserState.AwaitingPaymentAmount;
            await _botClient.SendTextMessageAsync(
                chatId,
                "Iltimos, to'lov qilgan summangizni kiriting (so'mda):");
        }

        private static async Task ShowBalance(long chatId, User user)
        {
            await _botClient.SendTextMessageAsync(
                chatId,
                $"💰 Sizning balansingiz: {user.Balance} so'm");
        }

        private static async Task ShowUserCabinet(long chatId, User user)
        {
            var endDate = user.PaymentDate.AddMonths(1);
            var daysLeft = (endDate - DateTime.Now).Days;
            var status = user.IsActive ? "🟢 Faol" : "🔴 Nofaol";

            var message = $"👤 Sizning kabinetingiz:\n\n" +
                          $"🆔 ID: <code>{user.Id}</code>\n" +
                          $"📊 Holat: {status}\n" +
                          $"💰 Balans: {user.Balance} so'm\n" +
                          $"📅 Keyingi to'lov: {endDate:dd.MM.yyyy}\n" +
                          $"⏳ Qolgan kunlar: {daysLeft}\n\n" +
                          $"🔗 Referal havola: https://t.me/PaioidBot?start={user.Id}";

            await _botClient.SendTextMessageAsync(
                chatId,
                message,
                parseMode: ParseMode.Html);
        }

        #endregion

        #region To'lov Tasdiqlash

        private static async Task ProcessUserState(long chatId, string text, UserState state, Message message)
        {
            switch (state)
            {
                case UserState.AwaitingPaymentAmount:
                    if (decimal.TryParse(text, out var amount))
                    {
                        _db.SetTempData(chatId, "payment_amount", amount.ToString());
                        _userStates[chatId] = UserState.AwaitingPaymentReceipt;
                        await _botClient.SendTextMessageAsync(
                            chatId,
                            $"Iltimos, to'lov cheki rasmini yuboring (summa: {amount} so'm)");
                    }
                    break;

                case UserState.AwaitingPaymentReceipt:
                    if (message.Photo != null && message.Photo.Length > 0)
                    {
                        var photoId = message.Photo.Last().FileId;

                        await HandlePaymentVerification(chatId, decimal.Parse(_db.GetTempData(chatId, "payment_amount")), photoId);
                        _userStates.Remove(chatId);
                    }
                    break;

                case UserState.AwaitingChannelId:
                    if (long.TryParse(text, out var channelId))
                    {
                        _db.AddChannel(channelId);
                        await _botClient.SendTextMessageAsync(chatId, "Kanal muvaffaqiyatli qo'shildi!");
                        _userStates.Remove(chatId);
                    }
                    break;

                case UserState.AwaitingNewPrice:
                    if (decimal.TryParse(text, out var newPrice))
                    {
                        _config.MonthlyFee = newPrice;
                        await _botClient.SendTextMessageAsync(chatId, $"Yangi narx: {newPrice} so'm");
                        _userStates.Remove(chatId);
                    }
                    break;
            }
        }

        private static async Task HandlePaymentVerification(long chatId, decimal amount, string receiptPhotoId)
        {
            var user = _db.GetUser(chatId);
            if (user == null) return;

            // Check if the paid amount matches the required monthly fee  
            if (amount < _config.MonthlyFee)
            {
                await _botClient.SendMessage(chatId,
                    $"To'langan summa ({amount} so'm) oylik obuna narxidan ({_config.MonthlyFee} so'm) kam. Iltimos, to'liq to'lovni amalga oshiring.");
                return;
            }

            // Admin notification  
            foreach (var adminId in _config.AdminIds)
            {
                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                   new[]
                   {
                       InlineKeyboardButton.WithCallbackData("✅ Qabul qilish", $"approve_{chatId}"),
                       InlineKeyboardButton.WithCallbackData("✏️ Tahrirlash", $"edit_{chatId}")
                   },
                   new[]
                   {
                       InlineKeyboardButton.WithCallbackData("❌ Rad etish", $"reject_{chatId}")
                   }
               });

                await _botClient.SendPhoto(
                    adminId,
                    receiptPhotoId,
                    caption: $"Yangi to'lov so'rovi!\n\n" +
                             $"👤 Foydalanuvchi: {user.Name}\n" +
                             $"🆔 ID: {user.Id}\n" +
                             $"💵 Summa: {amount} so'm\n" +
                             $"📅 Sana: {DateTime.Now:dd.MM.yyyy HH:mm}",
                    replyMarkup: adminKeyboard);
            }

            await _botClient.SendMessage(
                chatId,
                "To'lovingiz adminlar tomonidan ko'rib chiqilmoqda. " +
                "Tasdiqlanganidan so'ng sizga xabar beramiz.");
        }

        private static async Task ApprovePayment(long userId, long adminId)
        {
            var user = _db.GetUser(userId);
            if (user == null) return;

            user.IsActive = true;
            user.PaymentDate = DateTime.Now;
            user.Balance -= _config.MonthlyFee;

            await _botClient.SendTextMessageAsync(
                adminId,
                $"To'lov tasdiqlandi! Foydalanuvchi {user.Name} obuna bo'ldi.");

            await _botClient.SendTextMessageAsync(
                userId,
                $"🎉 Tabriklaymiz! Sizning obunangiz tasdiqlandi.\n" +
                $"Keyingi to'lov sanasi: {DateTime.Now.AddMonths(1):dd.MM.yyyy}");
        }

        #endregion

        #region Ma'lumotlar Bazasi va Modellar

        public class Database
        {
            private List<User> _users = new();
            private List<Channel> _channels = new();
            private Dictionary<long, Dictionary<string, string>> _tempData = new();

            public User GetOrCreateUser(long chatId, Telegram.Bot.Types.User telegramUser)
            {
                var user = _users.FirstOrDefault(u => u.Id == chatId);
                if (user == null)
                {
                    user = new User
                    {
                        Id = chatId,
                        Name = $"{telegramUser.FirstName} {telegramUser.LastName}".Trim(),
                        Username = telegramUser.Username,
                        Balance = 0,
                        PaymentDate = DateTime.MinValue,
                        IsActive = false
                    };
                    _users.Add(user);
                }
                return user;
            }

            public User GetUser(long id) => _users.FirstOrDefault(u => u.Id == id);
            public void AddChannel(long id) => _channels.Add(new Channel { Id = id });
            public void SetTempData(long userId, string key, string value)
            {
                if (!_tempData.ContainsKey(userId))
                    _tempData[userId] = new Dictionary<string, string>();
                _tempData[userId][key] = value;
            }
            public string GetTempData(long userId, string key) =>
                _tempData.TryGetValue(userId, out var data) && data.TryGetValue(key, out var value) ? value : null;
        }

        public class User
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Username { get; set; }
            public decimal Balance { get; set; }
            public DateTime PaymentDate { get; set; }
            public bool IsActive { get; set; }
        }

        public class Channel
        {
            public long Id { get; set; }
        }

        public class BotConfig
        {
            public decimal MonthlyFee { get; set; }
            public decimal ReferralBonus { get; set; }
            public List<long> AdminIds { get; set; }
        }

        public enum UserState
        {
            None,
            AwaitingPaymentAmount,
            AwaitingPaymentReceipt,
            AwaitingChannelId,
            AwaitingNewPrice,
            AwaitingEditAmount,
            AwaitingRejectionReason
        }

        #endregion

        private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Xato: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}