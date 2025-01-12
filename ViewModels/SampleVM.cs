using CommunityToolkit.Mvvm.Input;
using Dezolati.Models;
using Dezolati.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.ViewModels
{
    public partial class SampleVM
    {
        //FirestoreService firestoreService;
        //public ObservableCollection<SampleModel> SampleData { get; set; } = [];
        //public SampleVM(FirestoreService firestoreService)
        //{
        //    this.firestoreService = firestoreService;
        //}
        //[RelayCommand]
        //public async Task GetSampleData()
        //{
        //    var x = await firestoreService.GetSampleModels();
        //    foreach (var item in x)
        //    {
        //        SampleData.Add(item);
        //    }
        //}
        //[RelayCommand]
        //public async Task SaveSampleData()
        //{
        //    var sample = new SampleModel
        //    {
        //        Name = "Jmeno",
        //        Pivo = 0,
        //        CreatedAt = DateTime.Now
        //    };
        //    await firestoreService.InsertSampleModel(sample);
        //}
    }
}
