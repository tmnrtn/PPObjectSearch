using PPObjectSearch.Services;

namespace PPObjectSearch.Tests.Services;

/// <summary>A business rule's workflow XAML read as the designer's If / Else and actions.</summary>
public class BusinessRuleReaderTests
{
    private const string Crm = "Microsoft.Crm.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

    private static string Reference(string type, string displayName, string arguments, string properties = "", string key = "") =>
        $"""
        <mxswa:ActivityReference {key}AssemblyQualifiedName="Microsoft.Crm.Workflow.Activities.{type}, {Crm}" DisplayName="{displayName}">
          <mxswa:ActivityReference.Arguments>{arguments}</mxswa:ActivityReference.Arguments>
          <mxswa:ActivityReference.Properties>{properties}</mxswa:ActivityReference.Properties>
        </mxswa:ActivityReference>
        """;

    private static string Literal(string result, string type, string value) =>
        Reference("EvaluateExpression", "EvaluateExpression", $"""
            <InArgument x:TypeArguments="x:String" x:Key="ExpressionOperator">CreateCrmType</InArgument>
            <InArgument x:TypeArguments="s:Object[]" x:Key="Parameters">[New Object() {"{"} Microsoft.Xrm.Sdk.Workflow.WorkflowPropertyType.{type}, "{value}", "{type}" {"}"}]</InArgument>
            <InArgument x:TypeArguments="s:Object" x:Key="TargetEntity">[CreatedEntities("primaryEntity#Temp")]</InArgument>
            <OutArgument x:TypeArguments="x:Object" x:Key="Result">[{result}]</OutArgument>
            """);

    private static string Get(string attribute, string value) => $"""
        <mxswa:GetEntityProperty Attribute="{attribute}" Entity="[InputEntities(&quot;primaryEntity&quot;)]" EntityName="account" Value="[{value}]">
          <mxswa:GetEntityProperty.TargetType>
            <InArgument x:TypeArguments="s:Type"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:Object" /></InArgument>
          </mxswa:GetEntityProperty.TargetType>
        </mxswa:GetEntityProperty>
        """;

    private static string Evaluate(string op, string operand, string? parameter, string result) =>
        Reference("EvaluateCondition", "EvaluateCondition", $"""
            <InArgument x:TypeArguments="mxsq:ConditionOperator" x:Key="ConditionOperator">{op}</InArgument>
            {(parameter is null ? "<x:Null x:Key=\"Parameters\" />" : $"<InArgument x:TypeArguments=\"s:Object[]\" x:Key=\"Parameters\">[New Object() {{ {parameter} }}]</InArgument>")}
            <InArgument x:TypeArguments="x:Object" x:Key="Operand">[{operand}]</InArgument>
            <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[{result}]</OutArgument>
            """);

    private static string Then(string name, string activities) =>
        Reference("Composite", name, "", $"""
            <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />
            <sco:Collection x:TypeArguments="Activity" x:Key="Activities">{activities}</sco:Collection>
            """, key: "x:Key=\"Then\" ");

    private static string Branch(string name, string condition, string activities) =>
        Reference("ConditionBranch", name,
            $"""<InArgument x:TypeArguments="x:Boolean" x:Key="Condition">{condition}</InArgument>""",
            Then(name, activities) + """<x:Null x:Key="Else" /><x:Null x:Key="Description" />""");

    private const string Entity = "Entity=\"[InputEntities(&quot;primaryEntity&quot;)]\" EntityName=\"account\"";

    /// <summary>
    /// A rule as the designer saves it: "If credit limit contains data and category is Preferred,
    /// make credit hold required, lock name, set phone and show an error; else if no credit limit,
    /// hide fax; else unlock name and default the category".
    /// </summary>
    internal static readonly string Sample = $"""
        <Activity x:Class="XrmWorkflow1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
            xmlns:mcwb="clr-namespace:Microsoft.Crm.Workflow.BusinessEntities;assembly={Crm}"
            xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"
            xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"
            xmlns:mxsq="clr-namespace:Microsoft.Xrm.Sdk.Query;assembly=Microsoft.Xrm.Sdk, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"
            xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=9.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35"
            xmlns:s="clr-namespace:System;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
            xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
            xmlns:sco="clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
            xmlns:this="clr-namespace:" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <x:Members>
            <x:Property Name="InputEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
            <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
          </x:Members>
          <this:XrmWorkflow1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6.InputEntities>
            <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
          </this:XrmWorkflow1a2b3c4d5e6f47a8b9c0d1e2f3a4b5c6.InputEntities>
          <mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>
          <mxswa:Workflow>
            {Reference("ConditionSequence", "ConditionStep1: Check credit", """<InArgument x:TypeArguments="x:Boolean" x:Key="Wait">False</InArgument>""", $"""
              <sco:Collection x:TypeArguments="Variable" x:Key="Variables">
                <Variable x:TypeArguments="x:Boolean" Default="False" Name="ConditionBranchStep2_condition" />
                <Variable x:TypeArguments="x:Object" Name="ConditionBranchStep2_1" />
              </sco:Collection>
              <sco:Collection x:TypeArguments="Activity" x:Key="Activities">
                {Get("creditlimit", "ConditionBranchStep2_1")}
                {Evaluate("NotNull", "ConditionBranchStep2_1", null, "ConditionBranchStep2_2")}
                {Get("accountcategorycode", "ConditionBranchStep2_3")}
                {Literal("ConditionBranchStep2_4", "OptionSetValue", "1")}
                {Evaluate("Equal", "ConditionBranchStep2_3", "ConditionBranchStep2_4", "ConditionBranchStep2_5")}
                {Reference("EvaluateLogicalCondition", "EvaluateLogicalCondition", """
                    <InArgument x:TypeArguments="mxsq:LogicalOperator" x:Key="LogicalOperator">And</InArgument>
                    <InArgument x:TypeArguments="x:Boolean" x:Key="LeftOperand">[ConditionBranchStep2_2]</InArgument>
                    <InArgument x:TypeArguments="x:Boolean" x:Key="RightOperand">[ConditionBranchStep2_5]</InArgument>
                    <OutArgument x:TypeArguments="x:Boolean" x:Key="Result">[ConditionBranchStep2_condition]</OutArgument>
                    """)}
                {Branch("ConditionBranchStep2", "[ConditionBranchStep2_condition]", $"""
                    <mcwb:SetFieldRequiredLevel DisplayName="SetFieldRequiredLevelStep1" ControlId="creditonhold" ControlType="standard" {Entity} RequiredLevel="Required" />
                    <mcwb:SetDisplayMode DisplayName="SetDisplayModeStep1" ControlId="name" ControlType="standard" {Entity} IsReadOnly="True" />
                    {Literal("SetAttributeValueStep1_1", "String", "000")}
                    <mxswa:SetEntityProperty Attribute="telephone1" {Entity} Value="[SetAttributeValueStep1_1]">
                      <mxswa:SetEntityProperty.TargetType>
                        <InArgument x:TypeArguments="s:Type"><mxswa:ReferenceLiteral x:TypeArguments="s:Type" Value="x:String" /></InArgument>
                      </mxswa:SetEntityProperty.TargetType>
                    </mxswa:SetEntityProperty>
                    <mcwb:SetAttributeValue DisplayName="SetAttributeValueStep1" {Entity} />
                    {Literal("SetMessageStep1_1", "String", "Credit limit is too high")}
                    <mcwb:SetMessage DisplayName="SetMessageStep1" ControlId="creditlimit" ControlType="standard" {Entity} />
                    """)}
                {Get("creditlimit", "ConditionBranchStep3_1")}
                {Evaluate("Null", "ConditionBranchStep3_1", null, "ConditionBranchStep3_condition")}
                {Branch("ConditionBranchStep3", "[ConditionBranchStep3_condition]", $"""
                    <mcwb:SetVisibility DisplayName="SetVisibilityStep1" ControlId="fax" ControlType="standard" {Entity} IsVisible="False" />
                    """)}
                {Branch("ConditionBranchStep4", "True", $"""
                    <mcwb:SetDisplayMode DisplayName="SetDisplayModeStep2" ControlId="name" ControlType="standard" {Entity} IsReadOnly="False" />
                    {Literal("SetAttributeValueStep2_1", "OptionSetValue", "2")}
                    <mxswa:SetEntityProperty Attribute="accountcategorycode" {Entity} Value="[SetAttributeValueStep2_1]" />
                    <mcwb:SetDefaultValue DisplayName="SetDefaultValueStep1" {Entity} />
                    <mcwb:SetFieldRequiredLevel DisplayName="SetFieldRequiredLevelStep2" ControlId="websiteurl" ControlType="standard" {Entity} RequiredLevel="Recommended" />
                    <mcwb:SetFieldRequiredLevel DisplayName="SetFieldRequiredLevelStep3" ControlId="fax" ControlType="standard" {Entity} RequiredLevel="None" />
                    """)}
              </sco:Collection>
              """)}
          </mxswa:Workflow>
        </Activity>
        """;

    [Fact]
    public void A_rule_reads_as_its_conditions_and_actions()
    {
        var steps = BusinessRuleReader.Read(Sample);

        Assert.Equal(
        [
            "If creditlimit contains data AND accountcategorycode equals 1",
            "    Make creditonhold required",
            "    Lock name",
            "    Set telephone1 value to 000",
            "    Show error message on creditlimit: \"Credit limit is too high\"",
            "Else if creditlimit does not contain data",
            "    Hide fax",
            "Else",
            "    Unlock name",
            "    Set default value of accountcategorycode to 2",
            "    Make websiteurl recommended",
            "    Make fax not required"
        ], steps.Select(s => s.Indented));
        Assert.Equal([BusinessRuleReader.If, BusinessRuleReader.ElseIf, BusinessRuleReader.Else],
            steps.Where(s => s.Depth == 0).Select(s => s.Kind));
        Assert.All(steps.Where(s => s.Depth == 1), s => Assert.Equal(BusinessRuleReader.Action, s.Kind));
    }

    [Fact]
    public void An_unconditional_rule_and_an_unknown_named_step_are_read()
    {
        var xaml = $"""
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:a="clr-namespace:Microsoft.Crm.Workflow.BusinessEntities;assembly={Crm}" xmlns:b="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow" xmlns:sco="clr-namespace:System.Collections.ObjectModel;assembly=mscorlib">
              <b:Workflow>
                <Persist />
                <a:SetVisibility ControlId="name" ControlType="standard" IsVisible="True" />
                <a:SomethingNew DisplayName="Do something new" />
                {Reference("ConditionBranch", "ConditionBranchStep1", """<InArgument x:TypeArguments="x:Boolean" x:Key="Condition">[unknown]</InArgument>""",
                    Then("ConditionBranchStep1", """<a:SetDisplayMode ControlId="name" IsReadOnly="[True]" />"""))
                    .Replace("mxswa:", "b:")}
              </b:Workflow>
            </Activity>
            """;

        Assert.Equal(
        [
            new BusinessRuleStep(0, BusinessRuleReader.Action, "Show name"),
            new BusinessRuleStep(0, BusinessRuleReader.Other, "Do something new"),
            new BusinessRuleStep(0, BusinessRuleReader.If, "If (condition)"),
            new BusinessRuleStep(1, BusinessRuleReader.Action, "Lock name")
        ], BusinessRuleReader.Read(xaml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<Activity><unclosed")]
    [InlineData("not xml at all")]
    [InlineData("<Activity xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"><Sequence><Assign /></Sequence></Activity>")]
    public void Unreadable_or_empty_xaml_gives_no_steps(string? xaml)
    {
        Assert.Empty(BusinessRuleReader.Read(xaml));
    }
}
