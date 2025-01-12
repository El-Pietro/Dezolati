using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dezolati.Models
{
    [FirestoreData]
    public class SampleModel
    {
        [FirestoreProperty]
        public string Id { get; set; }
        [FirestoreProperty]
        public string Name { get; set; }
        [FirestoreProperty]
        public int Pivo { get; set; }
        [FirestoreProperty]
        public DateTime CreatedAt { get; set; }
    }



}
