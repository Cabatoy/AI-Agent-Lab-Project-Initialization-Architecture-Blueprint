variable "region" {
  type        = string
  description = "The verified OCI tenancy home region. No implicit cross-region fallback."
  default     = "me-abudhabi-1"
  validation {
    condition     = var.region == "me-abudhabi-1"
    error_message = "This plan is scoped only to verified home region me-abudhabi-1."
  }
}

variable "oci_profile" {
  type        = string
  description = "A locally authenticated, short-lived CLI security-token profile in OCI Cloud Shell."
  default     = "DEFAULT"
}

variable "tenancy_ocid" {
  type        = string
  description = "Privately supplied tenancy root OCID. Never commit a real value or state file."
  default     = ""
  sensitive   = true
}

variable "proposal_network" {
  type        = bool
  description = "Look up the existing compartment and plan private VCN/subnet only; OFF by default."
  default     = false
}

variable "proposal_vm" {
  type        = bool
  description = "Plan an A1 VM only; OFF by default, requires all review gates."
  default     = false
}

variable "proposal_bastion" {
  type        = bool
  description = "Propose a scoped Bastion and VM-only SSH NSG; OFF by default."
  default     = false
}

variable "proposal_bastion_iam" {
  type        = bool
  description = "Propose the separately reviewed operator IAM policy; OFF by default."
  default     = false
}

variable "bastion_client_cidr" {
  type        = string
  description = "One reviewed operator source IPv4 /32, supplied privately; no default access."
  default     = ""
  sensitive   = true
}

variable "bastion_operator_group_ocid" {
  type        = string
  description = "Existing reviewed operator group OCID, supplied privately; no group is created."
  default     = ""
  sensitive   = true
}

variable "owner_free_eligibility_checked" {
  type        = bool
  default     = false
  description = "Owner verified account-specific Always Free eligibility/quota and current shape cost."
}

variable "owner_billing_reviewed" {
  type        = bool
  default     = false
  description = "Owner verified exact cost and absence of paid resources/egress/backups."
}

variable "owner_private_management_reviewed" {
  type        = bool
  default     = false
  description = "Owner reviewed a safe private management path; it is not provisioned in this proposal."
}

variable "image_provenance_reviewed" {
  type        = bool
  default     = false
  description = "Owner independently checked official image publisher, ARM64 architecture and exact OCID."
}

variable "availability_domain" {
  type        = string
  default     = ""
  description = "Exact account-specific AD name supplied privately, only if VM proposal is enabled."
}

variable "ubuntu_2404_arm_image_ocid" {
  type        = string
  default     = ""
  sensitive   = true
  description = "Pinned verified official Ubuntu 24.04 ARM64 image OCID, privately supplied."
}

variable "operator_ssh_public_key" {
  type        = string
  default     = ""
  sensitive   = true
  description = "PUBLIC SSH key from an approved personal device (never a private key)."
}
