using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo(
    assemblyName: "WallstopStudios.DxMessaging.Tests.Runtime",
    AllInternalsVisible = true
)]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Editor")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Reflex")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.VContainer")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Zenject")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Runtime")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Runtime.Reflex")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Runtime.VContainer")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Runtime.Zenject")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Editor")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.Editor.Allocations")]
[assembly: InternalsVisibleTo("WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks")]

#if UNITY_2021_3_OR_NEWER || DXMESSAGING_SEPARATE_ATTRIBUTES
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxAotMessageAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxAutoConstructorAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxBroadcastMessageAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxIgnoreMissingBaseCallAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxOptionalParameterAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxTargetedMessageAttribute))]
[assembly: TypeForwardedTo(typeof(DxMessaging.Core.Attributes.DxUntargetedMessageAttribute))]
#endif
