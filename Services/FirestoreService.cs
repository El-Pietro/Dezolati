using Dezolati.Models;
using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.Services
{
    public class FirestoreService
    {
        private FirestoreDb db;

        private async Task SetupFirestore()
        {
            if (db == null)
            {
                var stream = await FileSystem.OpenAppPackageFileAsync("dezolati-firebase-adminsdk-s46cq-76d411c057.json");
                var reader = new StreamReader(stream);
                {
                    var contents = reader.ReadToEnd();

                    db = new FirestoreDbBuilder
                    {
                        ProjectId = "dezolati",
                        ConverterRegistry = new ConverterRegistry
                    {
                        new DateTimeToTimestampConverter(),
                    },
                        JsonCredentials = contents
                    }.Build();
                }
            }
        }

        public async Task<int> GetTotalDrinksByUser(string userId, int DrinkType)
        {
            await SetupFirestore();

            var snapshot = await db.Collection("Drinks")
                                    .WhereEqualTo("UserId", userId)
                                    .WhereEqualTo("DrinkType", DrinkType)
                                    .GetSnapshotAsync();
            int totalDrinks = snapshot.Documents
                .Select(doc => doc.GetValue<int>("Quantity"))
                .Sum();
            return totalDrinks;


        }


        public async Task InsertDrinkModel(DrinkModel sample)
        {
            try
            {
                await SetupFirestore();
                await db.Collection("Drinks").AddAsync(sample);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při zápisu do Firestore: {ex.Message}");
            }
        }
        public async Task <List<DrinkModel>> GetDrinkModels()
        {
            try
            {
                await SetupFirestore();
                var data = await db
                                    .Collection("DrinkModels")
                                    .GetSnapshotAsync();
                var DrinkModels = data.Documents
                    .Select(doc =>
                    {
                        var DrinkModel = doc.ConvertTo<DrinkModel>();
                        DrinkModel.Id = doc.Id;
                        return DrinkModel;
                    })
                    .ToList();
                return DrinkModels;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při načítání dat z Firestore: {ex.Message}");
                return new List<DrinkModel>();
            }
        }
    }
}
