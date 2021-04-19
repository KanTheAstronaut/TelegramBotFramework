﻿using System;
using System.Collections.Generic;
using System.Text;
using TelegramBotFramework.Core.Interfaces;

namespace TelegramBotFramework.Core.SQLiteDb
{
    public class TelegramBotSetting:IEditableEntity<int>
    {
        /// <summary>
        /// DB Id of the setting
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Settings alias, can be loaded using launch parameters
        /// </summary>
        public string Alias { get; set; }
        /// <summary>
        /// The ID of the Telegram user who will be the main admin for the bot (typically, the person running the code)
        /// </summary>
        public int TelegramDefaultAdminUserId { get; set; }
        /// <summary>
        /// Your Telegram Bot API Token
        /// </summary>
        public string TelegramBotAPIKey { get; set; }
        public string ReadableEntityNameForEditing { get; set; } = "BotSetting";
        object IEditableEntity.Id { get => Id; set => Id=int.Parse(value.ToString()); }
    }
}
