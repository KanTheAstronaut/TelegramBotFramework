﻿using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotFramework.Core.Helpers;
using TelegramBotFramework.Core.Interfaces;
using TelegramBotFramework.Core.Logging;
using TelegramBotFramework.Core.Objects;
using TelegramBotFramework.Core.SQLiteDb;
using TelegramBotFramework.Core.SQLiteDb.Extensions;
using TgBotFramework.Core.Interfaces;

namespace TelegramBotFramework.Core
{
    public class TelegramBotWrapper : ITelegramBotWrapper
    {
        public static string RootDirectory => AppDomain.CurrentDomain.BaseDirectory;

        public bool AnswerHandling { get; set; }
        public UsersSurveys CurrentUserUpdatingObjects { get; set; } = new UsersSurveys();
        public delegate CommandResponse ChatCommandMethod(CommandEventArgs args);
        public delegate CommandResponse ChatServeyMethod(Message args);
        public Dictionary<long, Queue<SurveyAttribute>> UsersWaitingAnswers { get; set; } = new Dictionary<long, Queue<SurveyAttribute>>();
        public Dictionary<ChatSurvey, ChatServeyMethod> SurveyAnswersHandlers = new Dictionary<ChatSurvey, ChatServeyMethod>();
        public readonly List<string> Questions = new List<string>();
        public delegate CommandResponse CallbackCommandMethod(CallbackEventArgs args);
        public delegate void OnException(Exception unhandled);
        public event OnException ExceptionHappened;

        public Dictionary<ChatCommand, ChatCommandMethod> Commands = new Dictionary<ChatCommand, ChatCommandMethod>();
        public Dictionary<CallbackCommand, CallbackCommandMethod> CallbackCommands = new Dictionary<CallbackCommand, CallbackCommandMethod>();
        public Dictionary<TelegramBotModule, Type> Modules = new Dictionary<TelegramBotModule, Type>();
        public TelegramBotLogger Log;
        public TelegramBotDbContext Db;
        public TelegramBotSetting LoadedSetting;
        public ModuleMessenger Messenger = new ModuleMessenger();
        public TelegramBotClient Bot { get; set; }
        internal static User Me = null;
        public IServiceProvider ServiceProvider;

        public bool IsSurveyInitiated { get; set; }

        private readonly bool UserMustBeApprooved;

        /// <summary>
        /// Constructor. You may inject IServiceProvider to freely use you registered services in your modules
        /// </summary>
        /// <param name="key"></param>
        /// <param name="adminId"></param>
        /// <param name="serviceProvider"></param>
        /// <param name="alias"></param>
        public TelegramBotWrapper(string key, int adminId, IServiceProvider serviceProvider = null, string alias = "TelegramBotFramework", bool needNewUserApproove = false)
        {
            UserMustBeApprooved = needNewUserApproove;

            ServiceProvider = serviceProvider;
            using (var db = new TelegramBotDbContext(alias))
            {
                db.Database.EnsureCreated();
                if (!db.Users.Any(c => c.UserId == adminId))
                {
                    db.Users.Add(new TelegramBotUser() { IsBotAdmin = true, UserId = adminId });
                    db.SaveChanges();
                }
            }
            Log = new TelegramBotLogger(Path.Combine(RootDirectory, "Logs-" + alias));
            Db = new TelegramBotDbContext(alias);
            var setting = new TelegramBotSetting() { Alias = alias, TelegramDefaultAdminUserId = adminId, TelegramBotAPIKey = key };
            LoadedSetting = setting;
            Console.OutputEncoding = Encoding.UTF8;
            Messenger.MessageSent += MessengerOnMessageSent;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;

            var telegramBotModuleDir = Path.Combine(RootDirectory, "AddonModules-" + alias);

            WatchForNewModules(telegramBotModuleDir);
        }
        private void WatchForNewModules(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            FileSystemWatcher watcher = new FileSystemWatcher();
            watcher.Path = path;
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Filter = "*.dll";
            watcher.Changed += new FileSystemEventHandler(OnChanged);
            watcher.EnableRaisingEvents = true;
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            Log.WriteLine("Scanning Addon TelegramBotModules directory for custom TelegramBotModules...", overrideColor: ConsoleColor.Cyan);
            LoadModules();
        }

        public void LoadModules()
        {
            //Clear the list
            Commands.Clear();
            //load base methods first
            GetMethodsFromAssembly(Assembly.GetExecutingAssembly());
            Log.WriteLine("Scanning Addon TelegramBotModules directory for custom TelegramBotModules...", overrideColor: ConsoleColor.Cyan);
            var telegramBotModuleDir = Path.Combine(RootDirectory, "AddonModules-" + LoadedSetting.Alias);
            Directory.CreateDirectory(telegramBotModuleDir);

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            //now load TelegramBotModules from directory
            foreach (var file in Directory.GetFiles(telegramBotModuleDir, "*.dll"))
            {
                GetMethodsFromAssembly(Assembly.LoadFrom(file));
            }
            foreach (var assembly in assemblies)
            {
                GetMethodsFromAssembly(assembly);
            }
        }

        private void GetMethodsFromAssembly(Assembly assembly)
        {
            try
            {
                var typs = assembly.GetTypes().Where(myType => myType.IsClass && !myType.IsAbstract
                   && myType.IsDefined(typeof(TelegramBotModule)));
                foreach (var type in assembly.GetTypes().Where(myType => myType.IsClass && !myType.IsAbstract
                   && myType.IsDefined(typeof(TelegramBotModule)) /*|| myType.IsAssignableFrom(typeof(ITelegramBotModule))*/))
                {
                   
                    var constructor = type.GetConstructor(new[] { this.GetType() });
                    if (constructor == null)
                    {                        
                        constructor = type.GetConstructor(new[] { typeof(TelegramBotWrapper) });
                    }

                    var instance = constructor.Invoke(new object[] { this });
                    var meths = instance.GetType().GetMethods();
                    var tAtt = type.GetCustomAttributes<TelegramBotModule>().First();
                    if (Modules.ContainsKey(tAtt))
                    {
                        Log.WriteLine($"{tAtt.Name} has been already loaded. Rename it, if it is no dublicate");
                        continue;
                    }
                    Modules.Add(tAtt, type);

                    foreach (var method in instance.GetType().GetMethods().Where(x => x.IsDefined(typeof(ChatSurvey))))
                    {
                        var att = method.GetCustomAttributes<ChatSurvey>().First();
                        if (SurveyAnswersHandlers.ContainsKey(att))
                        {
                            Log.WriteLine($"ChatSurvey {method.Name}\n\t  already added", overrideColor: ConsoleColor.Cyan);
                            continue;
                        }
                        SurveyAnswersHandlers.Add(att, (ChatServeyMethod)Delegate.CreateDelegate(typeof(ChatServeyMethod), instance, method));
                        Log.WriteLine($"Loaded SurveyAnswersHandler {method.Name}\n", overrideColor: ConsoleColor.Green);

                    }
                    foreach (var method in instance.GetType().GetMethods().Where(x => x.IsDefined(typeof(ChatCommand))))
                    {
                        var att = method.GetCustomAttributes<ChatCommand>().First();
                        if (Commands.ContainsKey(att))
                        {
                            Log.WriteLine($"ChatCommand {method.Name}\n\t  with Trigger(s): {att.Triggers.Aggregate((a, b) => a + ", " + b)} not loaded, possible dublicate", overrideColor: ConsoleColor.Cyan);
                            continue;
                        }
                        Commands.Add(att, (ChatCommandMethod)Delegate.CreateDelegate(typeof(ChatCommandMethod), instance, method));
                        Log.WriteLine($"Loaded ChatCommand {method.Name}\n\t Trigger(s): {att.Triggers.Aggregate((a, b) => a + ", " + b)}", overrideColor: ConsoleColor.Green);

                    }
                    foreach (var m in type.GetMethods().Where(x => x.IsDefined(typeof(CallbackCommand))))
                    {
                        var att = m.GetCustomAttributes<CallbackCommand>().First();
                        if (CallbackCommands.ContainsKey(att))
                        {
                            Log.WriteLine($"Not loaded CallbackCommand {m.Name}\n\t Trigger: {att.Trigger}, possible dublicate", overrideColor: ConsoleColor.Cyan);
                            continue;
                        }
                        CallbackCommands.Add(att, (CallbackCommandMethod)Delegate.CreateDelegate(typeof(CallbackCommandMethod), instance, m));
                        Log.WriteLine($"Loaded CallbackCommand {m.Name}\n\t Trigger: {att.Trigger}", overrideColor: ConsoleColor.Green);
                    }


                }
            }
            catch (Exception e)
            {
                Log.WriteLine(e.ToString(), LogLevel.Error, fileName: "error.log");
            }
        }
        private void MessengerOnMessageSent(object sender, EventArgs e)
        {
            var args = (e as MessageSentEventArgs);
            Send(args);
        }

        private void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            Log.WriteLine((e.ExceptionObject as Exception).Message, LogLevel.Error);
            if (ex != null)
                ExceptionHappened(ex);
        }

        public void Run()
        {
            Bot = new TelegramBotClient(LoadedSetting.TelegramBotAPIKey);
            try
            {
                //Load in the modules              
                LoadModules();
                Bot.DeleteWebhookAsync();
                Me = Bot.GetMeAsync().Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Log.WriteLine("502 bad gateway, restarting in 2 seconds", LogLevel.Error, fileName: "telegram.log");
                Log.WriteLine(ex.ToString(), LogLevel.Error, fileName: "telegram.log");

                Thread.Sleep(TimeSpan.FromSeconds(2));

            }
            Bot.OnUpdate += BotOnUpdateReceived;
            Bot.OnInlineQuery += BotOnOnInlineQuery;
            Bot.OnCallbackQuery += BotOnOnCallbackQuery;

            Bot.StartReceiving();

            Log.WriteLine("Connected to Telegram and listening..." + Me.FirstName + Me.LastName);
        }

        private void BotOnOnCallbackQuery(object sender, CallbackQueryEventArgs callbackQueryEventArgs)
        {
            var query = callbackQueryEventArgs.CallbackQuery;

            //extract the trigger
            var trigger = query.Data.Split('|')[0];
            var args = query.Data.Replace(trigger + "|", "");
            var user = UserHelper.GetTelegramUser(Db, cbQuery: query);
            if (user.Grounded) return;
            Log.WriteLine(query.From.FirstName, LogLevel.Info, ConsoleColor.Cyan, "telegram.log");
            Log.WriteLine(query.Data, LogLevel.Info, ConsoleColor.White, "telegram.log");
            foreach (var callback in CallbackCommands)
            {
                if (String.Equals(callback.Key.Trigger, trigger, StringComparison.InvariantCultureIgnoreCase))
                {
                    var eArgs = new CallbackEventArgs()
                    {
                        SourceUser = user,
                        DatabaseInstance = Db,
                        Parameters = args,
                        Target = query.Message.Chat.Id.ToString(),
                        Messenger = Messenger,
                        Bot = Bot,
                        Query = query
                    };
                    var response = callback.Value.Invoke(eArgs);
                    if (!String.IsNullOrWhiteSpace(response?.Text))
                    {
                        Send(response, query.Message, true);
                    }
                }
            }
        }

        private void BotOnOnInlineQuery(object sender, InlineQueryEventArgs inlineQueryEventArgs)
        {
            try
            {
                var query = inlineQueryEventArgs.InlineQuery;

                new Thread(() => HandleQuery(query)).Start();
            }
            catch (Exception e)
            {
                while (e.InnerException != null)
                    e = e.InnerException;
                var message = e.GetType() + " - " + e.Message;
                if (e is FileNotFoundException)
                    message += " file: " + ((FileNotFoundException)e).FileName;
                //else if (e is DirectoryNotFoundException)
                //    message += " file: " + ((DirectoryNotFoundException)e).;
                message += Environment.NewLine + e.StackTrace;
                Log.WriteLine($"Error in message handling: {message}", LogLevel.Error, fileName: "telegram.log");
            }
        }

        private async void HandleQuery(InlineQuery query)
        {
            var user = UserHelper.GetTelegramUser(Db, null, query);
            if (user.Grounded)
            {
                await Bot.AnswerInlineQueryAsync(query.Id, new InlineQueryResultBase[]
                {
                    new InlineQueryResultArticle("0", "Nope!", new InputTextMessageContent("I did bad things, and now I'm grounded from the bot."))
                    {
                        Description = "You are grounded...",
                    }
                }, 0, true);
                return;
            }
            Log.WriteLine("INLINE QUERY", LogLevel.Info, ConsoleColor.Cyan, "telegram.log");
            Log.WriteLine(user.Name + ": " + query.Query, LogLevel.Info, ConsoleColor.White, "telegram.log");
            var com = GetParameters("/" + query.Query);
            var choices =
                Commands.Where(x => x.Key.DevOnly != true && x.Key.BotAdminOnly != true && x.Key.GroupAdminOnly != true & !x.Key.HideFromInline & !x.Key.DontSearchInline &&
                x.Key.Triggers.Any(t => t.ToLower().Contains(com[0].ToLower())) & !x.Key.DontSearchInline).ToList();
            choices.AddRange(Commands.Where(x => x.Key.DontSearchInline && x.Key.Triggers.Any(t => String.Equals(t, com[0], StringComparison.InvariantCultureIgnoreCase))));
            if (LoadedSetting.TelegramDefaultAdminUserId == user.UserId)
                choices.AddRange(Commands.Where(x => (x.Key.DevOnly || x.Key.BotAdminOnly) && x.Key.AllowInlineAdmin && x.Key.Triggers.Any(t => t == com[0])));
            if (user.IsBotAdmin)
                choices.AddRange(Commands.Where(x => x.Key.BotAdminOnly && x.Key.AllowInlineAdmin && x.Key.Triggers.Any(t => t == com[0])));

            var results = new List<InlineQueryResultBase>();
            foreach (var c in choices)
            {
                var response = c.Value.Invoke(new CommandEventArgs
                {
                    SourceUser = user,
                    DatabaseInstance = Db,
                    Parameters = com[1],
                    Target = "",
                    Messenger = Messenger,
                    Bot = Bot,
                    Message = null
                });
                var title = c.Key.Triggers[0];
                var description = c.Key.HelpText;
                if (query.Query.Split(' ').Length > 1 || c.Key.DontSearchInline || c.Key.Triggers.Any(x => String.Equals(x, com[0], StringComparison.InvariantCultureIgnoreCase)))
                {
                    description = response.ImageDescription ?? description;
                    title = response.ImageTitle ?? title;
                }

                results.Add(new InlineQueryResultArticle(Commands.ToList().IndexOf(c).ToString(), title, new InputTextMessageContent(response.Text)
                {
                    DisableWebPagePreview = false,
                    ParseMode = response.ParseMode
                })
                {
                    Description = description,
                    Title = title,
                    ThumbUrl = response.ImageUrl,
                    Url = response.ImageUrl,
                    HideUrl = true
                });

            }
            var menu = results.ToArray();
            try
            {
                await Bot.AnswerInlineQueryAsync(query.Id, menu, 0, true);
            }
            catch (AggregateException e)
            {
                Console.WriteLine(e.InnerExceptions[0].Message);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }

        }
        private void BotOnUpdateReceived(object sender, UpdateEventArgs updateEventArgs)
        {
            try
            {
                var update = updateEventArgs.Update;
                if (update.Type == UpdateType.InlineQuery) return;
                if (update.Type == UpdateType.CallbackQuery) return;
                if (!(update.Message?.Date > DateTime.UtcNow.AddSeconds(-15)))
                {
                    //Log.WriteLine("Ignoring message due to old age: " + update.Message.Date);
                    return;
                }
                new Thread(() => Handle(update)).Start();
            }
            catch (Exception e)
            {
                while (e.InnerException != null)
                    e = e.InnerException;
                var message = e.GetType() + " - " + e.Message;
                if (e is FileNotFoundException)
                    message += " file: " + ((FileNotFoundException)e).FileName;
                //else if (e is DirectoryNotFoundException)
                //    message += " file: " + ((DirectoryNotFoundException)e).;
                message += Environment.NewLine + e.StackTrace;
                Log.WriteLine($"Error in message handling: {message}", LogLevel.Error, fileName: "telegram.log");
            }
        }


        internal void Handle(Update update)
        {
            if (update.Message.Type == MessageType.Text)
            {
                //TODO: do something with this update
                try
                {
                    var msg = (update.Message.From.Username ?? update.Message.From.FirstName) + ": " + update.Message.Text;
                    var chat = update.Message.Chat.Title;
                    if (String.IsNullOrWhiteSpace(chat))
                        chat = "Private Message";

                    var user = UserHelper.GetTelegramUser(Db, update);

                    if (user.Grounded) return;
                    TelegramBotGroup group;
                    if (update.Message.Chat.Type != ChatType.Private)
                    {
                        group = GroupHelper.GetGroup(Db, update);
                    }
                    Log.WriteLine(chat, LogLevel.Info, ConsoleColor.Cyan, "telegram.log");
                    Log.WriteLine(msg, LogLevel.Info, ConsoleColor.White, "telegram.log");

                    if (UserMustBeApprooved)
                    {
                        if (!user.IsBotAdmin)
                        {
                            Bot.SendTextMessageAsync(update.Message.Chat, "You must be approved to use this bot, write to admin");
                            return;
                        }
                    }
                    //if (!AnswerHandling && IsSurveyInitiated)
                    //{
                    //    if (UserMustBeApprooved)
                    //    {
                    //        if (!user.IsBotAdmin)
                    //        {
                    //            Bot.SendTextMessageAsync(update.Message.Chat, "You must be approved to use this bot, write to admin");
                    //            return;
                    //        }
                    //    }
                    //    Send(SendQuestion(update.Message.Chat.Id), update.Message, false);
                    //    IsSurveyInitiated = false;
                    //    //  Send(new MessageSentEventArgs() { Target = update.Message.Chat.Id.ToString(), Response = SendQuestion(update.Message.Chat.Id) });
                    //    return;
                    //}
                    if (AnswerHandling && UsersWaitingAnswers[update.Message.Chat.Id].Count > 0)
                    {
                        if (!SurveyAnswersHandlers.Any())
                        {
                            Send(new MessageSentEventArgs() { Target = LoadedSetting.TelegramDefaultAdminUserId.ToString(), Response = new CommandResponse("Here is any answer handlers") });
                            return;
                        }
                        //  Send(GetAnswer(update.Message.Chat.Id, update.Message.Text), update.Message, true);

                        //Send(new MessageSentEventArgs() { Target = update.Message.Chat.Id.ToString(), Response = GetAnswer(update.Message.Chat.Id, update.Message.Text) });
                        //Send(new MessageSentEventArgs() { Target = update.Message.Chat.Id.ToString(), Response = GetAnswer(update.Message) });
                        if (SurveyAnswersHandlers.Any(c => c.Key.Name == UsersWaitingAnswers[update.Message.Chat.Id].GetType().Name))
                        {
                            var customAnswerHandler = SurveyAnswersHandlers.FirstOrDefault(c => c.Key.Name == UsersWaitingAnswers[update.Message.Chat.Id].GetType().Name);
                            var response = customAnswerHandler.Value.Invoke(update.Message);
                            Send(response, update.Message);
                        }
                        else
                        {
                            var customAnswerHandler = SurveyAnswersHandlers.FirstOrDefault();
                            var response = customAnswerHandler.Value.Invoke(update.Message);
                            Send(response, update.Message);
                        }
                        //foreach (var answerHandler in SurveyAnswersHandlers)
                        //{
                        //    if (answerHandler.Key.Name == UsersWaitingAnswers[update.Message.Chat.Id].GetType().Name)
                        //        answerHandler.Value.Invoke(update.Message);

                        //}
                        return;
                    }


                    if (update.Message.Text.StartsWith("!") || update.Message.Text.StartsWith("/"))
                    {
                        var args = GetParameters(update.Message.Text);
                        foreach (var command in Commands)
                        {
                            if (command.Key.Triggers.Contains(args[0].ToLower()))
                            {
                                //check for access
                                var att = command.Key;
                                if (att.DevOnly &&
                                    update.Message.From.Id != LoadedSetting.TelegramDefaultAdminUserId)
                                {

                                    Send(new CommandResponse("You are not the developer!"), update);
                                    return;

                                }
                                if (att.BotAdminOnly & !user.IsBotAdmin & LoadedSetting.TelegramDefaultAdminUserId != update.Message.From.Id)
                                {
                                    Send(new CommandResponse("You are not a bot admin!"), update);
                                    return;
                                }
                                if (att.GroupAdminOnly)
                                {
                                    if (update.Message.Chat.Type == ChatType.Private)
                                    {
                                        Send(new CommandResponse("You need to run this in a group"), update);
                                        return;
                                    }
                                    //is the user an admin of the group?
                                    var status =
                                        Bot.GetChatMemberAsync(update.Message.Chat.Id, update.Message.From.Id)
                                            .Result.Status;
                                    if (status != ChatMemberStatus.Administrator && status != ChatMemberStatus.Creator)
                                    {
                                        Send(new CommandResponse("You are not a group admin!"), update);
                                        return;
                                    }
                                }
                                if (att.InGroupOnly && update.Message.Chat.Type == ChatType.Private)
                                {
                                    Send(new CommandResponse("You need to run this in a group"), update);
                                    return;
                                }
                                if (att.InPrivateOnly)
                                {
                                    Send(new CommandResponse("You need to run this in private"), update);
                                    return;
                                }
                                var eArgs = new CommandEventArgs
                                {
                                    SourceUser = user,
                                    DatabaseInstance = Db,
                                    Parameters = args[1],
                                    Target = update.Message.Chat.Id.ToString(),
                                    Messenger = Messenger,
                                    Bot = Bot,
                                    Message = update.Message
                                };
                                var response = command.Value.Invoke(eArgs);
                                if (!String.IsNullOrWhiteSpace(response.Text))
                                    Send(response, update);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.WriteLine("Exception happend at handling update:\n" + ex.ToString(), LogLevel.Error, ConsoleColor.Cyan, "error.log");
                }
            }
        }


        private static string[] GetParameters(string input)
        {
            if (String.IsNullOrEmpty(input)) return new[] { "", "" };
            var result = input.Contains(" ") ? new[] { input.Substring(1, input.IndexOf(" ")).Trim(), input.Substring(input.IndexOf(" ") + 1) } : new[] { input.Substring(1).Trim(), null };
            result[0] = result[0].Replace("@" + Me.Username, "");
            return result;
        }

        public void Send(CommandResponse response, Update update, bool edit = false)
        {
            Send(response, update.Message, edit);
        }

        public void Send(CommandResponse response, Message update, bool edit = false)
        {
            var text = response.Text;
            Log.WriteLine("Replying: " + text, overrideColor: ConsoleColor.Yellow);
            try
            {
                if (text.StartsWith("/me"))
                {
                    text = text.Replace("/me", "*") + "*";
                }
                var targetId = response.Level == ResponseLevel.Public ? update.Chat.Id : update.From.Id;
                if (edit && targetId == update.Chat.Id)
                {
                    Bot.EditMessageTextAsync(targetId, update.MessageId, text,
                        replyMarkup: CreateMarkupFromMenu(response.Menu),
                        parseMode: response.ParseMode);
                }
                else
                {
                    Bot.SendTextMessageAsync(targetId, text, replyMarkup: CreateMarkupFromMenu(response.Menu),
                        parseMode: response.ParseMode);
                }
                //Bot.SendTextMessage(update.Message.Chat.Id, text);
                return;
            }
            catch
            {

            }
        }


        public void Send(MessageSentEventArgs args)
        {
            Log.WriteLine("Replying: " + args.Response.Text, overrideColor: ConsoleColor.Yellow);
            var text = args.Response.Text;
            try
            {
                if (text.StartsWith("/me"))
                {
                    text = text.Replace("/me", "*") + "*";
                }
                if (text.StartsWith("/"))
                {
                    text = text.Substring(1);
                }

                if (long.TryParse(args.Target, out var targetId))
                {
                    var r = Bot.SendTextMessageAsync(targetId, text, replyMarkup: CreateMarkupFromMenu(args.Response.Menu), parseMode: args.Response.ParseMode).Result;
                }
                //Bot.SendTextMessage(update.Message.Chat.Id, text);
                return;
            }
            catch (AggregateException e)
            {
                Console.WriteLine(e.InnerExceptions[0].Message);
            }
            catch
            {

                //Logging.Write("Server error! restarting..");
                //Process.Start("csircbot.exe");
                //Environment.Exit(7);
            }
        }

        public InlineKeyboardMarkup CreateMarkupFromMenu(Menu menu)
        {
            if (menu == null) return null;
            var col = menu.Columns - 1;
            //this is gonna be fun...
            var final = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < menu.Buttons.Count; i++)
            {
                var row = new List<InlineKeyboardButton>();
                do
                {
                    var cur = menu.Buttons[i];
                    row.Add(new InlineKeyboardButton
                    {
                        Text = cur.Text,
                        CallbackData = $"{cur.Trigger}|{cur.ExtraData}",
                        Url = cur.Url
                    });
                    i++;
                    if (i == menu.Buttons.Count) break;
                } while (i % (col + 1) != 0);
                i--;
                final.Add(row.ToArray());
                if (i == menu.Buttons.Count) break;
            }
            return new InlineKeyboardMarkup(final.ToArray());
        }
    }
}
