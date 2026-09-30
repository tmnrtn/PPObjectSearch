using PPObjectSearch.Dataverse;

namespace PPObjectSearch.Tests.CoreAndModels;

public class ComponentTypesTests
{
    [Theory]
    [InlineData(1, "Table")]
    [InlineData(2, "Column")]
    [InlineData(9, "Choice")]
    [InlineData(20, "Security Role")]
    [InlineData(26, "View")]
    [InlineData(29, "Process")]
    [InlineData(60, "Form")]
    [InlineData(61, "Web Resource")]
    [InlineData(300, "Canvas App")]
    [InlineData(380, "Environment Variable Definition")]
    [InlineData(10029, "Connection Reference")]
    [InlineData(10088, "Workflow Binary")]
    public void GetName_returns_the_friendly_name_for_known_types(int type, string expected)
    {
        Assert.Equal(expected, ComponentTypes.GetName(type));
        Assert.Equal(expected, ComponentTypes.TryGetName(type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(-1)]
    [InlineData(99999)]
    public void GetName_falls_back_to_the_type_number_for_unknown_types(int type)
    {
        Assert.Equal($"Component type {type}", ComponentTypes.GetName(type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(99999)]
    public void TryGetName_returns_null_for_unknown_types(int type)
    {
        Assert.Null(ComponentTypes.TryGetName(type));
    }

    [Fact]
    public void GetName_gives_the_same_label_to_codes_that_share_one()
    {
        Assert.Equal(ComponentTypes.GetName(3), ComponentTypes.GetName(10));
        Assert.Equal(ComponentTypes.GetName(24), ComponentTypes.GetName(60));
        Assert.Equal(ComponentTypes.GetName(371), ComponentTypes.GetName(372));
    }

    [Theory]
    [InlineData(0, "Workflow (classic)")]
    [InlineData(1, "Dialog")]
    [InlineData(2, "Business Rule")]
    [InlineData(3, "Action")]
    [InlineData(4, "Business Process Flow")]
    [InlineData(5, "Cloud Flow")]
    [InlineData(6, "Desktop Flow")]
    [InlineData(7, "AI Flow")]
    public void GetProcessCategoryName_maps_workflow_categories(int category, string expected)
    {
        Assert.Equal(expected, ComponentTypes.GetProcessCategoryName(category));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(100)]
    public void GetProcessCategoryName_returns_null_for_unknown_categories(int category)
    {
        Assert.Null(ComponentTypes.GetProcessCategoryName(category));
    }

    [Theory]
    [InlineData(1, "Entity")]
    [InlineData(2, "Attribute")]
    [InlineData(9, "OptionSet")]
    [InlineData(26, "SavedQuery")]
    [InlineData(29, "Workflow")]
    [InlineData(60, "SystemForm")]
    [InlineData(61, "WebResource")]
    [InlineData(92, "SDKMessageProcessingStep")]
    [InlineData(300, "CanvasApp")]
    [InlineData(10018, "CustomAPI")]
    [InlineData(10029, "ConnectionReference")]
    public void GetSdkName_returns_the_option_set_member_name(int type, string expected)
    {
        Assert.Equal(expected, ComponentTypes.GetSdkName(type));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(24)]
    [InlineData(381)]
    [InlineData(99999)]
    public void GetSdkName_returns_null_for_unmapped_types(int type)
    {
        Assert.Null(ComponentTypes.GetSdkName(type));
    }
}
