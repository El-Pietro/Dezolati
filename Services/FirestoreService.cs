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

        public async Task<DateTime?> GetLastDrinkTime(string userId)
        {
            await SetupFirestore();

            var query = db.Collection("Drinks")
                          .WhereEqualTo("UserId", userId)
                          .OrderByDescending("DrinkAt")
                          .Limit(1);

            var snapshot = await query.GetSnapshotAsync();

            if (snapshot.Count > 0)
            {
                var lastDrink = snapshot.Documents[0].ConvertTo<DrinkModel>();
                return lastDrink.DrinkAt;
            }

            return null; // Nebyly nalezeny žádné drinky
        }


        public async Task<DailyDrinksModel> GetDailyDrinks(string userId, DateTime currentDate)
        {
            await SetupFirestore();

            // Získej čas posledního drinku
            var lastDrinkTime = await GetLastDrinkTime(userId);
            // Pokud nebyl žádný drink, použij standardní definici dne
            if (lastDrinkTime == null || lastDrinkTime.Value.Date != currentDate.Date)
            {
                return null;
            }
            // Pokud byl poslední drink mezi půlnocí a 9:00 a přes 3 hodiny zpět, začne nový den
            if (lastDrinkTime.Value.Hour >= 0 && lastDrinkTime.Value.Hour < 9)
            {
                if (DateTime.UtcNow - lastDrinkTime.Value > TimeSpan.FromHours(3))
                {
                    return null; // Den skončil, začíná nový
                }
            }
            //Načti záznam pro aktuální den
            var currentDateString = currentDate.ToString("dd-MM-yyyy");

            var query = db.Collection("DailyDrinks")
                                    .WhereEqualTo("UserId", userId)
                                    .WhereEqualTo("Date", currentDateString);
            var snapshot = await query.GetSnapshotAsync();

            if (snapshot.Count > 0)
            {
                var document = snapshot.Documents[0];
                var dailyDrink = document.ConvertTo<DailyDrinksModel>();

                // Nastavení ID dokumentu z Firestore
                dailyDrink.Id = document.Id;

                return dailyDrink;
            }

            return null; // Záznam pro dnešní den neexistuje
        }

        public async Task AddDrink(DrinkModel drink)
        {
            await SetupFirestore();
            try
            {
                await db.Collection("Drinks").AddAsync(drink);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při zápisu drinku: {ex.Message}");
            }
        }

        public async Task UpdateOrCreateDailyDrinks(string userId, DrinkModel drink)
        {
            // Načti existující denní záznam
            var currentDate = DateTime.UtcNow.Date;
            var dailyDrinks = await GetDailyDrinks(userId, currentDate);

            if (dailyDrinks != null)
            {
                // Aktualizace existujícího záznamu
                var updates = new Dictionary<string, object>
                {
                    { "Pivo", FieldValue.Increment(drink.Pivo) },
                    { "Vino", FieldValue.Increment(drink.Vino) },
                    { "Panaky", FieldValue.Increment(drink.Panaky) },
                    { "UpdateAt", DateTime.UtcNow }
                };

                var docRef = db.Collection("DailyDrinks").Document(dailyDrinks.Id);
                await docRef.UpdateAsync(updates);
            }
            else
            {
                // Vytvoření nového záznamu
                var newDailyDrinks = new DailyDrinksModel
                {
                    UserId = userId,
                    Date = currentDate.ToString("dd-MM-yyyy"),
                    CreatedAt = DateTime.UtcNow,
                    UpdateAt = DateTime.UtcNow,
                    Pivo = drink.Pivo,
                    Vino = drink.Vino,
                    Panaky = drink.Panaky
                };

                await db.Collection("DailyDrinks").AddAsync(newDailyDrinks);
            }
        }
        public async Task RecordDrink(string userId, string name, int pivo, int vino, int panaky)
        {
            // 1. Vytvoření záznamu do tabulky Drinks
            var newDrink = new DrinkModel
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Name = name,
                Pivo = pivo,
                Vino = vino,
                Panaky = panaky,
                DrinkAt = DateTime.UtcNow
            };

            await AddDrink(newDrink);

            // 2. Aktualizace nebo vytvoření denního součtu
            await UpdateOrCreateDailyDrinks(userId, newDrink);
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

        public async Task<UserModel> GetUserData(string userId)
        {
            await SetupFirestore();

            var docRef = db.Collection("Users").Document(userId);
            var snapshot = await docRef.GetSnapshotAsync();
            if (snapshot.Exists)
            {
                return snapshot.ConvertTo<UserModel>();
            }
            return null; // TF? Uživatel neexistuje
        }

        public async Task UpdateUserData(string userId, UserModel user)
        {
            await SetupFirestore();

            var docRef = db.Collection("Users").Document(userId);
            await docRef.SetAsync(user, SetOptions.MergeAll);
        }

    }
}
