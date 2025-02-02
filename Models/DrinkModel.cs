using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.Models
{
    [FirestoreData]
    public class DrinkModel
    {
        [FirestoreProperty]
        public string Id { get; set; }
        [FirestoreProperty]
        public string UserId { get; set; }
        [FirestoreProperty]
        public string Name { get; set; }
        [FirestoreProperty]
        public int Pivo { get; set; }
        [FirestoreProperty]
        public int Vino { get; set; }
        [FirestoreProperty]
        public int Panaky { get; set; }
        [FirestoreProperty]
        public DateTime DrinkAt { get; set; }
    }
    public class DateTimeToTimestampConverter : IFirestoreConverter<DateTime>
    {
        public object ToFirestore(DateTime value) => Timestamp.FromDateTime(value.ToUniversalTime());
        public DateTime FromFirestore(object value)
        {
            if (value is Timestamp timestamp)
            {
                return timestamp.ToDateTime();
            }
            throw new ArgumentException("Divná hodnota");
        }
    }

}
