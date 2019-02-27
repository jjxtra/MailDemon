﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MailDemon
{
    [Serializable]
    public class SubscribeModel : BaseModel
    {
        public string ListName { get; set; }
        public string Title { get; set; }
        public Dictionary<string, object> Fields { get; set; } = new Dictionary<string, object>();

        public object FieldValue(string name)
        {
            Fields.TryGetValue(name, out object value);
            return value;
        }
    }
}