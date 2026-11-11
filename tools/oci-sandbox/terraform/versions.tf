terraform {
  required_version = ">= 1.5.0"
  required_providers {
    oci = {
      source  = "oracle/oci"
      version = "= 9.11.0"
    }
  }
}

# SecurityToken: only use a reviewed, short-lived OCI Cloud Shell CLI profile.
# Do not place a private API key, session token or tenancy OCID in source control.
provider "oci" {
  region              = var.region
  auth                = "SecurityToken"
  config_file_profile = var.oci_profile
}
