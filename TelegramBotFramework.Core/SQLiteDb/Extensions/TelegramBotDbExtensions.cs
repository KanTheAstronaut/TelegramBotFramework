﻿using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotFramework.Core.Interfaces;

namespace TelegramBotFramework.Core.SQLiteDb.Extensions
{
    public static class TelegramBotDbExtensions
    {
        #region Users

        public static void Save(this TelegramBotUser u, ITelegramBotDbContext db)
        {
            if (u.ID == 0 || !ExistsInDb(u, db))
            {
                try
                {
                    db.Users.Add(u);
                    db.SaveChanges();
                    u.ID = db.Users.FirstOrDefault(c => c.UserId == u.UserId).ID;
                }
                catch { }
            }
            else
            {
                try
                {
                    db.Users.Update(u);
                    db.SaveChanges();
                }
                catch { }
            }
        }

        public static bool ExistsInDb(this TelegramBotUser user, ITelegramBotDbContext db)
        {
            return db.Users.AsNoTracking().Any(i => i.ID == user.ID);
        }

        public static void RemoveFromDb(this TelegramBotUser user, ITelegramBotDbContext db)
        {
            db.Users.Remove(user);
            db.SaveChanges();
        }

  
        #endregion

        #region Groups

        public static void Save(this TelegramBotGroup u, ITelegramBotDbContext db)
        {
            if (u.ID == null || !ExistsInDb(u, db))
            {                
                db.Groups.Add(u);
                db.SaveChanges();
                u.ID = db.Groups.FirstOrDefault(c => c.GroupId == u.GroupId).ID;
            }
            else
            {
                db.Groups.Update(u);
                db.SaveChanges();               
            }
        }

        public static bool ExistsInDb(this TelegramBotGroup group, ITelegramBotDbContext db)
        {
            return db.Groups.Any(c => c.ID == group.ID);
        }

        public static void RemoveFromDb(this TelegramBotGroup group, ITelegramBotDbContext db)
        {
            db.Groups.Remove(group);
            db.SaveChanges();
        }

       

        #endregion

        #region Settings

        public static void Save(this TelegramBotSetting set, ITelegramBotDbContext db)
        {
            if (set.ID == null || !ExistsInDb(set, db))
            {             
                db.Settings.Add(set);
                db.SaveChanges();
                set.ID = db.Settings.FirstOrDefault(c => c.Alias == set.Alias).ID;
            }
            else
            {               
                db.Settings.Update(set);
                db.SaveChanges();
            }
        }

        public static bool ExistsInDb(this TelegramBotSetting set, ITelegramBotDbContext db)
        {
            return db.Settings.Any(c => c.ID == set.ID);
        }

        public static void RemoveFromDb(this TelegramBotSetting set, ITelegramBotDbContext db)
        {
            db.Settings.Remove(set);
            db.SaveChanges();
        }

       

        #endregion

       

     

        public static string ToString(this Telegram.Bot.Types.User user)
        {
            return (user.FirstName + " " + user.LastName).Trim();
        }

        #region Helpers
        public static TelegramBotUser GetTarget(this Message message, string args, TelegramBotUser sourceUser, ITelegramBotDbContext db)
        {
            if (message == null) return sourceUser;
            if (message?.ReplyToMessage != null)
            {
                var m = message.ReplyToMessage;
                var userid = m.ForwardFrom?.Id ?? m.From.Id;
                return db.Users.AsNoTracking().FirstOrDefault(x => x.UserId == userid) ?? sourceUser;
            }
            if (String.IsNullOrWhiteSpace(args))
            {
                return sourceUser;
            }
            //check for a user mention
            var mention = message?.Entities.FirstOrDefault(x => x.Type == MessageEntityType.Mention);
            var textmention = message?.Entities.FirstOrDefault(x => x.Type == MessageEntityType.TextMention);
            var id = 0;
            var username = "";
            if (mention != null)
                username = message.Text.Substring(mention.Offset + 1, mention.Length - 1);
            else if (textmention != null)
            {
                id = textmention.User.Id;
            }
            TelegramBotUser result = null;
            if (!String.IsNullOrEmpty(username))
                result = db.Users.AsNoTracking().FirstOrDefault(
                    x => x.UserName.ToUpper() == username.ToUpper());
            else if (id != 0)
                result = db.Users.AsNoTracking().FirstOrDefault(x => x.UserId == id);
            else
                result = db.Users.AsNoTracking().ToList().FirstOrDefault(
                        x =>
                            String.Equals(x.UserId.ToString(), args, StringComparison.InvariantCultureIgnoreCase) ||
                            String.Equals(x.UserName, args.Replace("@", ""), StringComparison.InvariantCultureIgnoreCase));
            return result ?? sourceUser;
        }
        #endregion
    }
}
