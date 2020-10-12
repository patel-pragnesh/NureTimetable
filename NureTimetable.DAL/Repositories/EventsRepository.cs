﻿using Microsoft.AppCenter.Analytics;
using NureTimetable.Core.Extensions;
using NureTimetable.Core.Models.Consts;
using NureTimetable.Core.Models.Exceptions;
using NureTimetable.DAL.Helpers;
using NureTimetable.DAL.Models.Consts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;
using Cist = NureTimetable.DAL.Models.Cist;
using Local = NureTimetable.DAL.Models.Local;

namespace NureTimetable.DAL
{
    public static class EventsRepository
    {
        /// <summary>
        /// Returns events for one entity. Null if error occurs 
        /// </summary>
        public static async Task<Local.TimetableInfo> GetEvents(Local.SavedEntity entity, bool tryUpdate = false, DateTime? dateStart = null, DateTime? dateEnd = null)
        {
            Local.TimetableInfo timetable;
            if (tryUpdate)
            {
                if (dateStart is null || dateEnd is null)
                {
                    throw new ArgumentNullException($"{nameof(dateStart)} and {nameof(dateEnd)} must be set");
                }

                timetable = (await GetTimetableFromCist(entity, dateStart.Value, dateEnd.Value)).Timetable;
                if (timetable != null)
                {
                    return timetable;
                }
            }
            timetable = GetTimetableLocal(entity);
            return timetable;
        }

        #region Local
        public static Local.TimetableInfo GetTimetableLocal(Local.SavedEntity entity)
            => GetTimetableLocal(new List<Local.SavedEntity>() { entity }).FirstOrDefault();

        public static List<Local.TimetableInfo> GetTimetableLocal(List<Local.SavedEntity> entities)
        {
            var timetables = new List<Local.TimetableInfo>();
            if (entities is null)
            {
                return timetables;
            }
            foreach (Local.SavedEntity entity in entities)
            {
                Local.TimetableInfo timetableInfo = Serialisation.FromJsonFile<Local.TimetableInfo>(FilePath.SavedTimetable(entity.Type, entity.ID));
                if (timetableInfo is null)
                {
                    continue;
                }
                timetables.Add(timetableInfo);
            }
            return timetables;
        }

        private static void UpdateTimetableLocal(Local.TimetableInfo newTimetable)
        {
            Serialisation.ToJsonFile(newTimetable, FilePath.SavedTimetable(newTimetable.Entity.Type, newTimetable.Entity.ID));
        }

        #region Lesson Info
        public static void UpdateLessonsInfo(Local.SavedEntity entity, List<Local.LessonInfo> lessonsInfo)
        {
            Local.TimetableInfo timetable = GetTimetableLocal(entity);
            timetable.LessonsInfo = lessonsInfo;
            UpdateTimetableLocal(timetable);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                MessagingCenter.Send(Application.Current, MessageTypes.LessonSettingsChanged, entity);
            });
        }
        #endregion
        #endregion

        #region Cist
        public static async Task<(Local.TimetableInfo Timetable, Exception Exception)> GetTimetableFromCist(Local.SavedEntity entity, DateTime dateStart, DateTime dateEnd)
        {
            if (!SettingsRepository.CheckCistTimetableUpdateRights(new List<Local.SavedEntity> { entity }).Any())
            {
                return (null, null);
            }

            using var client = new HttpClient();
            try
            {
                Local.TimetableInfo timetable = GetTimetableLocal(entity) ?? new Local.TimetableInfo(entity);

                // Getting events
                Analytics.TrackEvent("Cist request", new Dictionary<string, string>
                {
                    { "Type", "GetTimetable" },
                    { "Subtype", entity.Type.ToString() },
                    { "Hour of the day", DateTime.Now.Hour.ToString() }
                });

                Uri uri = Urls.CistApiEntityTimetable(entity.Type, entity.ID, dateStart, dateEnd);
                string responseStr = GetHardcodedEventsFromCist();
                responseStr = responseStr.Replace("&amp;", "&");
                responseStr = responseStr.Replace("\"events\":[\n]}]", "\"events\": []");
                Cist.Timetable cistTimetable = CistHelper.FromJson<Cist.Timetable>(responseStr);

                // Check for valid results
                if (timetable.Events.Count != 0 && cistTimetable.Events.Count == 0)
                {
                    Analytics.TrackEvent("Received timetable is empty", new Dictionary<string, string>
                    {
                        { "Entity", $"{entity.Type} {entity.Name} ({entity.ID})" },
                        { "From", dateStart.ToString("dd.MM.yyyy") },
                        { "To", dateEnd.ToString("dd.MM.yyyy") }
                    });

                    return (null, null);
                }

                // Updating timetable information
                timetable.Events = cistTimetable.Events.Select(ev =>
                    {
                        Local.Event localEvent = MapConfig.Map<Cist.Event, Local.Event>(ev);
                        localEvent.Lesson = MapConfig.Map<Cist.Lesson, Local.Lesson>(cistTimetable.Lessons.First(l => l.Id == ev.LessonId));
                        localEvent.Type = MapConfig.Map<Cist.EventType, Local.EventType>(
                            cistTimetable.EventTypes.FirstOrDefault(et => et.Id == ev.TypeId) ?? Cist.EventType.UnknownType
                        );
                        localEvent.Teachers = cistTimetable.Teachers
                            .Where(t => ev.TeacherIds.Contains(t.Id))
                            .DistinctBy(t => t.ShortName.Replace('ї', 'i'))
                            .Select(t => MapConfig.Map<Cist.Teacher, Local.Teacher>(t))
                            .ToList();
                        localEvent.Groups = cistTimetable.Groups
                            .Where(g => ev.GroupIds.Contains(g.Id))
                            .Select(g => MapConfig.Map<Cist.Group, Local.Group>(g))
                            .ToList();
                        return localEvent;
                    })
                    .Distinct()
                    .ToList();

                // Saving timetables
                UpdateTimetableLocal(timetable);

                // Updating LastUpdated for saved groups 
                List<Local.SavedEntity> AllSavedEntities = UniversityEntitiesRepository.GetSaved();
                foreach (Local.SavedEntity savedEntity in AllSavedEntities.Where(e => e == entity))
                {
                    savedEntity.LastUpdated = DateTime.Now;
                }
                UniversityEntitiesRepository.UpdateSaved(AllSavedEntities);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    MessagingCenter.Send(Application.Current, MessageTypes.TimetableUpdated, entity);
                });

                return (timetable, null);
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ex.Data.Add("Entity", $"{entity.Type} {entity.Name} ({entity.ID})");
                    ex.Data.Add("From", dateStart.ToString("dd.MM.yyyy"));
                    ex.Data.Add("To", dateEnd.ToString("dd.MM.yyyy"));
                    MessagingCenter.Send(Application.Current, MessageTypes.ExceptionOccurred, ex);
                });
                return (null, ex);
            }
        }
        #endregion

        private static string GetHardcodedEventsFromCist()
        {
            return "{\"time-zone\":\"Europe/Kiev\",\"events\":[{\"subject_id\":1053567,\"start_time\":1569937200,\"end_time\":1569942900,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1569937200,\"end_time\":1569942900,\"type\":10,\"number_pair\":6,\"auditory\":\"205и\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1569943500,\"end_time\":1569949200,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1569943500,\"end_time\":1569949200,\"type\":10,\"number_pair\":7,\"auditory\":\"205и\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1569949800,\"end_time\":1569955500,\"type\":10,\"number_pair\":8,\"auditory\":\"205и\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1569949800,\"end_time\":1569955500,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570542000,\"end_time\":1570547700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570542000,\"end_time\":1570547700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570548300,\"end_time\":1570554000,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570548300,\"end_time\":1570554000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570554600,\"end_time\":1570560300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1570554600,\"end_time\":1570560300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571146800,\"end_time\":1571152500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571146800,\"end_time\":1571152500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571153100,\"end_time\":1571158800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571153100,\"end_time\":1571158800,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571159400,\"end_time\":1571165100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571159400,\"end_time\":1571165100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571751600,\"end_time\":1571757300,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571751600,\"end_time\":1571757300,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571757900,\"end_time\":1571763600,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571757900,\"end_time\":1571763600,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571764200,\"end_time\":1571769900,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1571764200,\"end_time\":1571769900,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572360000,\"end_time\":1572365700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572360000,\"end_time\":1572365700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572366300,\"end_time\":1572372000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572366300,\"end_time\":1572372000,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572372600,\"end_time\":1572378300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572372600,\"end_time\":1572378300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572964800,\"end_time\":1572970500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572964800,\"end_time\":1572970500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572971100,\"end_time\":1572976800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572971100,\"end_time\":1572976800,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572977400,\"end_time\":1572983100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1572977400,\"end_time\":1572983100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573569600,\"end_time\":1573575300,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573569600,\"end_time\":1573575300,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573575900,\"end_time\":1573581600,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573575900,\"end_time\":1573581600,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573582200,\"end_time\":1573587900,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1573582200,\"end_time\":1573587900,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574174400,\"end_time\":1574180100,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574174400,\"end_time\":1574180100,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574180700,\"end_time\":1574186400,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574180700,\"end_time\":1574186400,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574187000,\"end_time\":1574192700,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574187000,\"end_time\":1574192700,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574779200,\"end_time\":1574784900,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574779200,\"end_time\":1574784900,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574785500,\"end_time\":1574791200,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574785500,\"end_time\":1574791200,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574791800,\"end_time\":1574797500,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1574791800,\"end_time\":1574797500,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575384000,\"end_time\":1575389700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575384000,\"end_time\":1575389700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575390300,\"end_time\":1575396000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575390300,\"end_time\":1575396000,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575396600,\"end_time\":1575402300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575396600,\"end_time\":1575402300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575988800,\"end_time\":1575994500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575988800,\"end_time\":1575994500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575995100,\"end_time\":1576000800,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1575995100,\"end_time\":1576000800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576001400,\"end_time\":1576007100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576001400,\"end_time\":1576007100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576593600,\"end_time\":1576599300,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576593600,\"end_time\":1576599300,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576599900,\"end_time\":1576605600,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576599900,\"end_time\":1576605600,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576606200,\"end_time\":1576611900,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1576606200,\"end_time\":1576611900,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577198400,\"end_time\":1577204100,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577198400,\"end_time\":1577204100,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577204700,\"end_time\":1577210400,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577204700,\"end_time\":1577210400,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577211000,\"end_time\":1577216700,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577211000,\"end_time\":1577216700,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577803200,\"end_time\":1577808900,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577803200,\"end_time\":1577808900,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577809500,\"end_time\":1577815200,\"type\":10,\"number_pair\":7,\"auditory\":\"__1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577809500,\"end_time\":1577815200,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577815800,\"end_time\":1577821500,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1577815800,\"end_time\":1577821500,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579012800,\"end_time\":1579018500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579012800,\"end_time\":1579018500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579019100,\"end_time\":1579024800,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579019100,\"end_time\":1579024800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579025400,\"end_time\":1579031100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579025400,\"end_time\":1579031100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579617600,\"end_time\":1579623300,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579617600,\"end_time\":1579623300,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579623900,\"end_time\":1579629600,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579623900,\"end_time\":1579629600,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579630200,\"end_time\":1579635900,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1579630200,\"end_time\":1579635900,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580222400,\"end_time\":1580228100,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580222400,\"end_time\":1580228100,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580228700,\"end_time\":1580234400,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580228700,\"end_time\":1580234400,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580235000,\"end_time\":1580240700,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580235000,\"end_time\":1580240700,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580827200,\"end_time\":1580832900,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580827200,\"end_time\":1580832900,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580833500,\"end_time\":1580839200,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580833500,\"end_time\":1580839200,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580839800,\"end_time\":1580845500,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1580839800,\"end_time\":1580845500,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581432000,\"end_time\":1581437700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581432000,\"end_time\":1581437700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581438300,\"end_time\":1581444000,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581438300,\"end_time\":1581444000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581444600,\"end_time\":1581450300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1581444600,\"end_time\":1581450300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582036800,\"end_time\":1582042500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582036800,\"end_time\":1582042500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582043100,\"end_time\":1582048800,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582043100,\"end_time\":1582048800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582049400,\"end_time\":1582055100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582049400,\"end_time\":1582055100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582641600,\"end_time\":1582647300,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582641600,\"end_time\":1582647300,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582647900,\"end_time\":1582653600,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582647900,\"end_time\":1582653600,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582654200,\"end_time\":1582659900,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1582654200,\"end_time\":1582659900,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583246400,\"end_time\":1583252100,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583246400,\"end_time\":1583252100,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583252700,\"end_time\":1583258400,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583252700,\"end_time\":1583258400,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583259000,\"end_time\":1583264700,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583259000,\"end_time\":1583264700,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583851200,\"end_time\":1583856900,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583851200,\"end_time\":1583856900,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583857500,\"end_time\":1583863200,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583857500,\"end_time\":1583863200,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583863800,\"end_time\":1583869500,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1583863800,\"end_time\":1583869500,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584456000,\"end_time\":1584461700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584456000,\"end_time\":1584461700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584462300,\"end_time\":1584468000,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584462300,\"end_time\":1584468000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584468600,\"end_time\":1584474300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1584468600,\"end_time\":1584474300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585060800,\"end_time\":1585066500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585060800,\"end_time\":1585066500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585067100,\"end_time\":1585072800,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585067100,\"end_time\":1585072800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585073400,\"end_time\":1585079100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585073400,\"end_time\":1585079100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585662000,\"end_time\":1585667700,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585662000,\"end_time\":1585667700,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585668300,\"end_time\":1585674000,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585668300,\"end_time\":1585674000,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585674600,\"end_time\":1585680300,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1585674600,\"end_time\":1585680300,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586266800,\"end_time\":1586272500,\"type\":10,\"number_pair\":6,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586266800,\"end_time\":1586272500,\"type\":10,\"number_pair\":6,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586273100,\"end_time\":1586278800,\"type\":10,\"number_pair\":7,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586273100,\"end_time\":1586278800,\"type\":10,\"number_pair\":7,\"auditory\":\"ФІЛІЯ\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586279400,\"end_time\":1586285100,\"type\":10,\"number_pair\":8,\"auditory\":\"___1\",\"teachers\":[],\"groups\":[7712002]},{\"subject_id\":1053567,\"start_time\":1586279400,\"end_time\":1586285100,\"type\":10,\"number_pair\":8,\"auditory\":\"149\",\"teachers\":[],\"groups\":[7712002]}],\"groups\":[{\"id\":7712002,\"name\":\"10 кл-1\"}],\"teachers\":[],\"subjects\": [{\"id\":1053567,\"brief\":\"ЦДП\",\"title\":\"ЦДП\",\"hours\":[{\"type\":10,\"val\":1500,\"teachers\":[]}]}],\"types\": [{\"id\":0,\"short_name\":\"Лк\",\"full_name\":\"Лекція\",\"id_base\":0, \"type\":\"lecture\"},{\"id\":1,\"short_name\":\"У.Лк (1)\",\"full_name\":\"Уст. Лекція (1)\",\"id_base\":0, \"type\":\"lecture\"},{\"id\":2,\"short_name\":\"У.Лк\",\"full_name\":\"Уст. Лекція\",\"id_base\":0, \"type\":\"lecture\"},{\"id\":10,\"short_name\":\"Пз\",\"full_name\":\"Практичне заняття\",\"id_base\":10, \"type\":\"practice\"},{\"id\":12,\"short_name\":\"У.Пз\",\"full_name\":\"Уст. Практичне заняття\",\"id_base\":10, \"type\":\"practice\"},{\"id\":20,\"short_name\":\"Лб\",\"full_name\":\"Лабораторна робота\",\"id_base\":20, \"type\":\"laboratory\"},{\"id\":21,\"short_name\":\"Лб\",\"full_name\":\"Лабораторна ІОЦ\",\"id_base\":20, \"type\":\"laboratory\"},{\"id\":22,\"short_name\":\"Лб\",\"full_name\":\"Лабораторна кафедри\",\"id_base\":20, \"type\":\"laboratory\"},{\"id\":23,\"short_name\":\"У.Лб\",\"full_name\":\"Уст. Лабораторна ІОЦ\",\"id_base\":20, \"type\":\"laboratory\"},{\"id\":24,\"short_name\":\"У.Лб\",\"full_name\":\"Уст. Лабораторна кафедри\",\"id_base\":20, \"type\":\"laboratory\"},{\"id\":30,\"short_name\":\"Конс\",\"full_name\":\"Консультація\",\"id_base\":30, \"type\":\"consultation\"},{\"id\":40,\"short_name\":\"Зал\",\"full_name\":\"Залік\",\"id_base\":40, \"type\":\"test\"},{\"id\":41,\"short_name\":\"дзал\",\"full_name\":\"ЗалікД\",\"id_base\":40, \"type\":\"test\"},{\"id\":50,\"short_name\":\"Екз\",\"full_name\":\"Екзамен\",\"id_base\":50, \"type\":\"exam\"},{\"id\":51,\"short_name\":\"ЕкзП\",\"full_name\":\"ЕкзаменП\",\"id_base\":50, \"type\":\"exam\"},{\"id\":52,\"short_name\":\"ЕкзУ\",\"full_name\":\"ЕкзаменУ\",\"id_base\":50, \"type\":\"exam\"},{\"id\":53,\"short_name\":\"ІспКомб\",\"full_name\":\"Іспит комбінований\",\"id_base\":50, \"type\":\"exam\"},{\"id\":54,\"short_name\":\"ІспТест\",\"full_name\":\"Іспит тестовий\",\"id_base\":50, \"type\":\"exam\"},{\"id\":55,\"short_name\":\"мод\",\"full_name\":\"Модуль\",\"id_base\":50, \"type\":\"exam\"},{\"id\":60,\"short_name\":\"КП/КР\",\"full_name\":\"КП/КР\",\"id_base\":60, \"type\":\"course_work\"}]}";
        }
    }
}