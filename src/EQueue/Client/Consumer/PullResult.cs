﻿using System.Collections.Generic;
using EQueue.Common;

namespace EQueue.Client.Consumer
{
    public class PullResult
    {
        public PullStatus PullStatus { get; set; }
        public long NextBeginOffset { get; set; }
        public long MinOffset { get; set; }
        public long MaxOffset { get; set; }
        public IEnumerable<Message> Messages { get; set; }
    }
}
