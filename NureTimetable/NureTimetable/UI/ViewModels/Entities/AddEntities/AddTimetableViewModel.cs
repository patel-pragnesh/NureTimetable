﻿using NureTimetable.Core.Localization;
using NureTimetable.DAL;
using NureTimetable.UI.Helpers;
using System;
using System.Threading.Tasks;
using Xamarin.CommunityToolkit.ObjectModel;
using Xamarin.Essentials;
using Xamarin.Forms;
using static NureTimetable.DAL.UniversityEntitiesRepository;

namespace NureTimetable.UI.ViewModels.Entities
{
    public class AddTimetableViewModel : BaseViewModel
    {
        #region Properties
        public IAsyncCommand PageAppearingCommand { get; }
        public IAsyncCommand UpdateCommand { get; }

        private bool updateCommandEnabled = true;
        public bool UpdateCommandEnabled { get => updateCommandEnabled; set { updateCommandEnabled = value; UpdateCommand.RaiseCanExecuteChanged(); } }

        public AddGroupViewModel AddGroupPageViewModel { get; } = new();
        public AddTeacherViewModel AddTeacherPageViewModel { get; } = new();
        public AddRoomViewModel AddRoomPageViewModel { get; } = new();
        #endregion

        public AddTimetableViewModel()
        {
            TimeSpan? timePass = DateTime.Now - SettingsRepository.Settings.LastCistAllEntitiesUpdate;
            bool isNeedReloadFromCist = !UniversityEntitiesRepository.IsInitialized && timePass > TimeSpan.FromDays(25);
            if (isNeedReloadFromCist)
            {
                Task.Run(UpdateFromCist);
            }

            PageAppearingCommand = CommandFactory.Create(() => UpdateEntitiesOnAllTabs());
            UpdateCommand = CommandFactory.Create(UpdateEntities, () => UpdateCommandEnabled);
        }

        private async Task UpdateEntities()
        {
            if (SettingsRepository.CheckCistAllEntitiesUpdateRights() == false)
            {
                await Shell.Current.DisplayAlert(LN.UniversityInfoUpdate, LN.UniversityInfoUpToDate, LN.Ok);
                return;
            }

            if (await Shell.Current.DisplayAlert(LN.UniversityInfoUpdate, LN.UniversityInfoUpdateConfirm, LN.Yes, LN.Cancel))
            {
                UpdateCommandEnabled = false;
                var updateFromCist = UniversityEntitiesRepository.UpdateFromCist();
                await UpdateEntitiesOnAllTabs(updateFromCist);
                await DisplayUpdateResult(await updateFromCist);
                UpdateCommandEnabled = true;
            }
        }

        private Task UpdateEntitiesOnAllTabs(Task updateDataSource = null)
        {
            return Task.WhenAll(
                AddGroupPageViewModel.UpdateEntities(updateDataSource),
                AddTeacherPageViewModel.UpdateEntities(updateDataSource),
                AddRoomPageViewModel.UpdateEntities(updateDataSource)
            );
        }

        private static async Task DisplayUpdateResult(UniversityEntitiesCistUpdateResult updateResult)
        {
            string message;
            if (updateResult.IsAllFail)
            {
                message = LN.UniversityInfoUpdateFail;
                if (updateResult.IsConnectionIssues)
                {
                    message = LN.CannotGetDataFromCist;
                }
                else if (updateResult.IsCistException)
                {
                    message = LN.CistException;
                }
            }
            else if (!updateResult.IsAllSuccessful)
            {
                string failedEntities = Environment.NewLine;
                if (updateResult.GroupsException != null)
                {
                    failedEntities += LN.Groups + Environment.NewLine;
                }
                if (updateResult.TeachersException != null)
                {
                    failedEntities += LN.Teachers + Environment.NewLine;
                }
                if (updateResult.RoomsException != null)
                {
                    failedEntities += LN.Rooms + Environment.NewLine;
                }
                message = string.Format(LN.UniversityInfoUpdatePartiallyFail, Environment.NewLine + failedEntities);
            }
            else
            {
                return;
            }

            await Shell.Current.DisplayAlert(LN.UniversityInfoUpdate, message, LN.Ok);
        }
    }
}
