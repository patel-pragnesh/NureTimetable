﻿using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using NureTimetable.Core.Localization;
using NureTimetable.Core.Models.Consts;
using NureTimetable.Core.Models.Settings;
using NureTimetable.DAL;
using NureTimetable.UI.Views;
using Syncfusion.Licensing;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Xamarin.CommunityToolkit.Helpers;
using Xamarin.Essentials;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

[assembly: XamlCompilation(XamlCompilationOptions.Compile)]
namespace NureTimetable
{
    public partial class App : Application
    {
        public App()
        {
            //Register Syncfusion license
            SyncfusionLicenseProvider.RegisterLicense(Keys.SyncfusionLicenseKey);

            // Set user selected language for the app
            CultureInfo culture = CultureInfo.CurrentCulture;
            if (SettingsRepository.Settings.Language != AppLanguage.FollowSystem)
            {
                culture = new CultureInfo((int)SettingsRepository.Settings.Language);
            }
            LocalizationResourceManager.Current.PropertyChanged += (_, _) => LN.Culture = LocalizationResourceManager.Current.CurrentCulture;
            LocalizationResourceManager.Current.Init(LN.ResourceManager, culture);

            Bugfix.InitCalendarCrashFix();
            VersionTracking.Track();

            InitializeComponent();
            MainPage = new AppShell();
        }

        protected override async void OnStart()
        {
            StartAppCenterLogging();

            // Log currect timetable view mode
            Analytics.TrackEvent("Timetable view mode", new Dictionary<string, string>
            {
                { nameof(SettingsRepository.Settings.TimetableViewMode), SettingsRepository.Settings.TimetableViewMode.ToString() }
            });

            if (SettingsRepository.Settings.IsDebugMode && await Crashes.HasCrashedInLastSessionAsync())
            {
                var report = await Crashes.GetLastSessionCrashReportAsync();
                await Shell.Current.DisplayAlert(LN.ErrorDetails, report.StackTrace, LN.Ok);
            }
        }

        [Conditional("RELEASE")]
        private static void StartAppCenterLogging()
        {
            if (DeviceInfo.DeviceType != DeviceType.Virtual)
            {
                AppCenter.Start(Keys.MicrosoftAppCenterKey, typeof(Analytics), typeof(Crashes));
            }
        }

        protected override void OnSleep()
        {
        }

        protected override void OnResume()
        {
        }
    }
}
