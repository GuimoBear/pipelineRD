﻿using PipelineRD.Tests.Request;
using System.Threading.Tasks;

namespace PipelineRD.Tests.Handlers
{
    public class InvalidSampleHandler : Handler<ContextSample, SampleRequest>
    {
        public override async Task Handle(SampleRequest _)
        {
            throw new System.NotImplementedException();
        }
    }
}
