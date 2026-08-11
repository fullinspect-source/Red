namespace InspectionEditor.Services
{
    public class EnergyComplianceInfo
    {
        public string? DisplayName { get; set; }
        public string? StatusText { get; set; }
        public string? DesignAirflowCfm { get; set; }
        public string? DesignAirflowCfm2 { get; set; }
        public string? DesignAirflowSource { get; set; }
        public string? DesignAirflowSourceFile { get; set; }
        public string? DesignAirflowOutdoorModel { get; set; }
        public string? DesignAirflowIndoorModel { get; set; }
        public string? DesignAirflowOutdoorModel2 { get; set; }
        public string? DesignAirflowIndoorModel2 { get; set; }
        internal string? DesignAirflowFallbackCfm { get; set; }
        internal string? DesignAirflowFallbackCfm2 { get; set; }
        internal string? DesignAirflowFallbackStatusText { get; set; }
        internal string? DesignAirflowFallbackDisplayName { get; set; }
    }
}
