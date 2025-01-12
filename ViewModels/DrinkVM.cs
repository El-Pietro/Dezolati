using CommunityToolkit.Mvvm.Input;
using Dezolati.Models;
using Dezolati.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.ViewModels
{
    public partial class DrinkVM
    {
        FirestoreService firestoreService;
        public ObservableCollection<DrinkModel> DrinkData { get; set; } = [];
        public DrinkVM(FirestoreService firestoreService)
        {
            this.firestoreService = firestoreService;
        }
        [RelayCommand]
        public async Task GetDrinkData()
        {
            var x = await firestoreService.GetDrinkModels();
            foreach (var item in x)
            {
                DrinkData.Add(item);
            }
        }
        [RelayCommand]
        public async Task SaveDrinkData()
        {
            var drink = new DrinkModel
            {
                UserId = SignInModel.ID,
                Name = SignInModel.Username,
                DrinkType = 1,
                Quantity = 1,
                DrinkAt = DateTime.Now
            };
            await firestoreService.InsertDrinkModel(drink);
        }
    }
}
