﻿using NureTimetable.Core.Localization;
using NureTimetable.DAL.Models.Local;
using NureTimetable.UI.ViewModels.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xamarin.Forms;

namespace NureTimetable.UI.ViewModels.Lessons
{
    public class LessonInfoViewModel : BaseViewModel
    {
        #region Variables
        private readonly TimetableInfo timetableInfo;
        #endregion

        #region Properties
        public LessonInfo LessonInfo { get; }

        public string Statistics { get; }
        #endregion

        public LessonInfoViewModel(INavigation navigation, LessonInfo lessonInfo, TimetableInfo timetableInfo) : base(navigation)
        {
            LessonInfo = lessonInfo;
            this.timetableInfo = timetableInfo;

            Title = LN.LessonInfo;
            Statistics = GetStatistics(timetableInfo.Events.Where(e => e.Lesson == lessonInfo.Lesson));
        }
        
        private string GetStatistics(IEnumerable<Event> events)
        {
            var statForTypes = timetableInfo.EventTypes(LessonInfo.Lesson.ID).OrderBy(et => et.ShortName).Select(et =>
            {
                var eventsWithType = events.Where(e => e.Type == et).ToList();
                return $"{et.ShortName}:\n" +
                    $"- {LN.EventsTotal} {eventsWithType.Count}, {eventsWithType.Where(e => e.Start > DateTime.Now).Count()} {LN.EventsLeft}\n" +
                    $"- {LN.NextEvent}: {eventsWithType.Where(e => e.Start > DateTime.Now).FirstOrDefault()?.Start.Date.ToShortDateString() ?? "-" }\n" +
                    $"- {LN.Teachers}: {string.Join(", ", eventsWithType.SelectMany(e => e.Teachers).Distinct().Select(t => t.ShortName).OrderBy(tn => tn).DefaultIfEmpty("-"))}";
            });
            return string.Join("\n", statForTypes);
        }
    }
}
