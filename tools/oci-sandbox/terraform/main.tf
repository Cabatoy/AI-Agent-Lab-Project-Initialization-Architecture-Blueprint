# Review-only OCI proposal. All resources are OFF unless operator explicitly
# changes the flags. Terraform plan is not approval to terraform apply.
# No internet/NAT/service gateway, public IP or executor endpoint is defined.

data "oci_identity_compartments" "hic" {
  count                     = var.proposal_network ? 1 : 0
  compartment_id            = var.tenancy_ocid
  compartment_id_in_subtree = false
  name                      = "hic-sandbox"
  state                     = "ACTIVE"

  lifecycle {
    precondition {
      condition     = var.owner_free_eligibility_checked && var.owner_billing_reviewed && startswith(var.tenancy_ocid, "ocid1.tenancy.")
      error_message = "BLOCKED: verified owner/account free eligibility, price, and tenancy identifier are required even to propose resources."
    }
    postcondition {
      condition     = length(self.compartments) == 1 && alltrue([for c in self.compartments : c.name == "hic-sandbox" && c.compartment_id == var.tenancy_ocid && c.state == "ACTIVE" && startswith(c.id, "ocid1.compartment.")])
      error_message = "BLOCKED: exactly one ACTIVE hic-sandbox direct child of the specified tenancy is required."
    }
  }
}

resource "oci_core_vcn" "hic" {
  count          = var.proposal_network ? 1 : 0
  compartment_id = one(data.oci_identity_compartments.hic[0].compartments).id
  cidr_block     = "10.92.0.0/16"
  display_name   = "hic-private-vcn"
  dns_label      = "hicvcn"
  freeform_tags  = { "hic-purpose" = "sandbox-poc" }
}

resource "oci_core_security_list" "deny_all" {
  count          = var.proposal_network ? 1 : 0
  compartment_id = one(data.oci_identity_compartments.hic[0].compartments).id
  vcn_id         = oci_core_vcn.hic[0].id
  display_name   = "hic-deny-all"
  # No ingress_security_rules and no egress_security_rules blocks.
}

resource "oci_core_subnet" "private" {
  count                      = var.proposal_network ? 1 : 0
  compartment_id             = one(data.oci_identity_compartments.hic[0].compartments).id
  vcn_id                     = oci_core_vcn.hic[0].id
  cidr_block                 = "10.92.1.0/24"
  display_name               = "hic-private-subnet"
  dns_label                  = "hicsubnet"
  prohibit_public_ip_on_vnic = true
  security_list_ids          = [oci_core_security_list.deny_all[0].id]
}

# VM proposal defaults OFF; if ever enabled it can be unreachable because
# network is deny-all. Secure admin/Bastion connection requires separate review.
resource "oci_core_instance" "sandbox_host" {
  count               = var.proposal_vm ? 1 : 0
  availability_domain = var.availability_domain
  compartment_id      = try(one(data.oci_identity_compartments.hic[0].compartments).id, null)
  display_name        = "hic-sandbox-host-01"
  shape               = "VM.Standard.A1.Flex"

  shape_config {
    ocpus         = 2
    memory_in_gbs = 8
  }

  create_vnic_details {
    subnet_id        = try(oci_core_subnet.private[0].id, null)
    assign_public_ip = false
  }

  instance_options {
    are_legacy_imds_endpoints_disabled = true
  }

  source_details {
    source_type             = "image"
    source_id               = var.ubuntu_2404_arm_image_ocid
    boot_volume_size_in_gbs = 50
  }

  metadata = {
    ssh_authorized_keys = var.operator_ssh_public_key
  }
  preserve_boot_volume = false
  freeform_tags        = { "hic-purpose" = "sandbox-poc", "hic-phase" = "proposal" }

  lifecycle {
    prevent_destroy = true
    precondition {
      condition = (
        var.proposal_network &&
        var.owner_free_eligibility_checked &&
        var.owner_billing_reviewed &&
        var.owner_private_management_reviewed &&
        var.image_provenance_reviewed &&
        length(trimspace(var.availability_domain)) > 0 &&
        startswith(var.ubuntu_2404_arm_image_ocid, "ocid1.image.") &&
        (startswith(trimspace(var.operator_ssh_public_key), "ssh-ed25519 ") ||
        startswith(trimspace(var.operator_ssh_public_key), "ssh-rsa "))
      )
      error_message = "BLOCKED: reviewed private network/cost/quota/management and pinned official image/AD/personal SSH PUBLIC key required."
    }
  }
}
