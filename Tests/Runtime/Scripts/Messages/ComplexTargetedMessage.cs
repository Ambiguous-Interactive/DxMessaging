namespace DxMessaging.Tests.Runtime.Scripts.Messages
{
    using System;
    using DxMessaging.Core.Attributes;

    [DxTargetedMessage]
    public partial struct ComplexTargetedMessage
    {
        // Guids are a bit beefy structure
        public readonly Guid firstId;
        public readonly Guid secondId;
        public readonly Guid thirdId;
        public readonly Guid fourthId;
        public readonly Guid fifthId;
        public readonly Guid sixthId;
        public readonly Guid seventhId;

        public ComplexTargetedMessage(Guid firstId)
        {
            this.firstId = firstId;
            secondId = Guid.NewGuid();
            thirdId = Guid.NewGuid();
            fourthId = Guid.NewGuid();
            fifthId = Guid.NewGuid();
            sixthId = Guid.NewGuid();
            seventhId = Guid.NewGuid();
        }
    }
}
