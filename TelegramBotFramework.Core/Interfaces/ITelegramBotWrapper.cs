﻿using System;
using System.Collections.Generic;
using System.Text;
using Telegram.Bot;
using TelegramBotFramework.Core.Objects;

namespace TelegramBotFramework.Core.Interfaces
{
    public interface ITelegramBotWrapper
    {
        void Run();
        bool AnswerHandling { get; set; }
      //  CommandResponse InitServey<T>(long userId) where T : class, new();
     //   event Action<long> OnSurveyComplete;
        Dictionary<long, Queue<SurveyAttribute>> UsersWaitingAnswers { get; set; }
        UsersSurveys CurrentUserUpdatingObjects { get; set; }
        bool IsSurveyInitiated { get; set; }
        TelegramBotClient Bot { get; set; }
    }
}
