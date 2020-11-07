﻿using NureTimetable.Core.Localization;
using NureTimetable.Core.Models.Consts;
using NureTimetable.DAL;
using NureTimetable.DAL.Models.Local;
using NureTimetable.UI.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace NureTimetable.UI.ViewModels.TimetableEntities
{
    public abstract class BaseAddEntityViewModel<T> : BaseViewModel
    {
        #region variables

        private protected List<T> _allEntities;

        private protected ObservableCollection<T> _entities;

        private protected bool _progressLayoutIsVisable;

        private protected bool _progressLayoutIsEnable;

        private protected bool _noSourceLayoutIsVisible;

        private protected string _searchBarText;

        private protected T _selectedEntity;

        #endregion

        #region Properties

        public ObservableCollection<T> Entities { get => _entities; private protected set => SetProperty(ref _entities, value); }

        public bool ProgressLayoutIsVisable
        {
            get => _progressLayoutIsVisable;
            set => SetProperty(ref _progressLayoutIsVisable, value);
        }

        public bool ProgressLayoutIsEnable
        {
            get => _progressLayoutIsEnable;
            set => SetProperty(ref _progressLayoutIsEnable, value);
        }

        public bool NoSourceLayoutIsVisible
        {
            get => _noSourceLayoutIsVisible;
            set => SetProperty(ref _noSourceLayoutIsVisible, value);
        }

        public string SearchBarText { get => _searchBarText; set => SetProperty(ref _searchBarText, value); }

        public ICommand SearchBarTextChangedCommand { get; }

        #endregion

        protected BaseAddEntityViewModel()
        {
            SearchBarTextChangedCommand = CommandHelper.Create(SearchBarTextChanged);

            MessagingCenter.Subscribe<Application>(this, MessageTypes.UniversityEntitiesUpdated, async (sender) =>
            {
                await UpdateEntities();
            });

            MainThread.BeginInvokeOnMainThread(async () => await UpdateEntities());
        }

        #region Abstract Methods

        protected abstract IOrderedEnumerable<T> OrderEntities();

        protected abstract IOrderedEnumerable<T> SearchEntities(string searchQuery);

        protected abstract List<T> GetAllEntities();

        #endregion

        #region Methods

        public T SelectedEntity
        {
            get => _selectedEntity;
            set
            {
                if (value != null)
                {
                    MainThread.BeginInvokeOnMainThread(async () => { await EntitySelected(value); });
                }

                _selectedEntity = value;
            }
        }

        protected abstract SavedEntity GetSavedEntity(T entity);

        protected async Task EntitySelected(T entity)
        {
            SavedEntity newEntity = GetSavedEntity(entity);

            List<SavedEntity> savedEntities = UniversityEntitiesRepository.GetSaved();
            if (savedEntities.Exists(e => e == newEntity))
            {
                await Shell.Current.DisplayAlert(LN.AddingTimetable, string.Format(LN.TimetableAlreadySaved, newEntity.Name), LN.Ok);
                return;
            }

            savedEntities.Add(newEntity);
            UniversityEntitiesRepository.UpdateSaved(savedEntities);

            await Shell.Current.DisplayAlert(LN.AddingTimetable, string.Format(LN.TimetableSaved, newEntity.Name), LN.Ok);
        }

        protected void SearchBarTextChanged()
        {
            if (_allEntities is null) return;

            if (string.IsNullOrEmpty(SearchBarText))
            {
                Entities = new ObservableCollection<T>(OrderEntities());
            }
            else
            {
                string searchQuery = SearchBarText.ToLower();
                Entities = new ObservableCollection<T>(SearchEntities(searchQuery));
            }
        }

        public async Task UpdateEntities(Task updateDataSource = null)
        {
            updateDataSource ??= Task.Run(UniversityEntitiesRepository.AssureInitialized);

            ProgressLayoutIsVisable = true;
            ProgressLayoutIsEnable = false;

            await updateDataSource;
            _allEntities = GetAllEntities();
            Entities = new ObservableCollection<T>(OrderEntities());
                
            NoSourceLayoutIsVisible = Entities.Count == 0;

            if (SearchBarTextChangedCommand.CanExecute(null))
            {
                SearchBarTextChangedCommand.Execute(null);
            }

            ProgressLayoutIsVisable = false;
            ProgressLayoutIsEnable = true;
        }

        #endregion
    }
}