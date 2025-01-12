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
                var stream = await FileSystem.OpenAppPackageFileAsync("dezolati-firebase-adminsdk-s46cq-e34ecf9d3b.json");
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
        public async Task InsertSampleModel(SampleModel sample)
        {
            try
            {
                await SetupFirestore();
                await db.Collection("SampleModels").AddAsync(sample);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při zápisu do Firestore: {ex.Message}");
            }
        }
        public async Task <List<SampleModel>> GetSampleModels()
        {
            try
            {
                await SetupFirestore();
                var data = await db
                                    .Collection("SampleModels")
                                    .GetSnapshotAsync();
                var sampleModels = data.Documents
                    .Select(doc =>
                    {
                        var sampleModel = doc.ConvertTo<SampleModel>();
                        sampleModel.Id = doc.Id;
                        return sampleModel;
                    })
                    .ToList();
                return sampleModels;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Chyba při načítání dat z Firestore: {ex.Message}");
                return new List<SampleModel>();
            }
        }
    }
}
