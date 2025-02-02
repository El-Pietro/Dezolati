using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.Models
{
    [FirestoreData]
    public class DailyDrinksModel
    {
        [FirestoreProperty]
        public string Id { get; set; }
        [FirestoreProperty]
        public string UserId { get; set; }
        [FirestoreProperty]
        public string Date { get; set; }
        [FirestoreProperty]
        public DateTime CreatedAt { get; set; }
        [FirestoreProperty]
        public DateTime UpdateAt { get; set; }
        [FirestoreProperty]
        public int Pivo { get; set; }
        [FirestoreProperty]
        public int Vino { get; set; }
        [FirestoreProperty]
        public int Panaky { get; set; }
    }
}
