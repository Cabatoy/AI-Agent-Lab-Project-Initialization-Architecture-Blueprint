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

locals {
  bastion_client_cidr_valid = can(regex("^([0-9]{1,3}\\.){3}[0-9]{1,3}/32$", var.bastion_client_cidr)) && try(cidrhost(var.bastion_client_cidr, 0) == trimsuffix(var.bastion_client_cidr, "/32"), false) && var.bastion_client_cidr != "0.0.0.0/32"
  bastion_group_valid       = can(regex("^ocid1\\.group\\.[A-Za-z0-9._-]+$", var.bastion_operator_group_ocid))
}

resource "oci_core_vcn" "hic" {
  count          = var.proposal_network ? 1 : 0
  compartment_id = one(data.oci_identity_compartments.hic[0].compartments).id
  cidr_block     = "10.92.0.0/16"
  is_ipv6enabled = false
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

resource "oci_bastion_bastion" "management" {
  count                        = var.proposal_bastion ? 1 : 0
  bastion_type                 = "standard"
  compartment_id               = try(one(data.oci_identity_compartments.hic[0].compartments).id, null)
  target_subnet_id             = try(oci_core_subnet.private[0].id, null)
  name                         = "hicmanagement"
  client_cidr_block_allow_list = [var.bastion_client_cidr]
  max_session_ttl_in_seconds   = 1800
  dns_proxy_status             = "DISABLED"

  lifecycle {
    precondition {
      condition = (
        var.proposal_network && var.owner_private_management_reviewed &&
        var.owner_free_eligibility_checked && var.owner_billing_reviewed &&
        local.bastion_client_cidr_valid
      )
      error_message = "BLOCKED: reviewed network, management, free eligibility, cost and a single operator IPv4 /32 are required."
    }
  }
}

resource "oci_core_network_security_group" "ssh_target" {
  count          = var.proposal_bastion ? 1 : 0
  compartment_id = try(one(data.oci_identity_compartments.hic[0].compartments).id, null)
  vcn_id         = try(oci_core_vcn.hic[0].id, null)
  display_name   = "hic-bastion-ssh-target"

  lifecycle {
    precondition {
      condition     = var.proposal_network
      error_message = "BLOCKED: Bastion SSH NSG requires the proposed private network."
    }
  }
}

resource "oci_core_network_security_group_security_rule" "bastion_ssh" {
  count                     = var.proposal_bastion ? 1 : 0
  network_security_group_id = oci_core_network_security_group.ssh_target[0].id
  direction                 = "INGRESS"
  protocol                  = "6"
  source_type               = "CIDR_BLOCK"
  source                    = "${oci_bastion_bastion.management[0].private_endpoint_ip_address}/32"
  stateless                 = false

  tcp_options {
    destination_port_range {
      min = 22
      max = 22
    }
  }
}

# VM and management remain disabled until a separately approved plan and review.
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
    assign_ipv6ip    = false
    nsg_ids          = var.proposal_bastion ? [oci_core_network_security_group.ssh_target[0].id] : []
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
        var.proposal_bastion &&
        var.owner_free_eligibility_checked &&
        var.owner_billing_reviewed &&
        var.owner_private_management_reviewed &&
        var.image_provenance_reviewed &&
        length(trimspace(var.availability_domain)) > 0 &&
        startswith(var.ubuntu_2404_arm_image_ocid, "ocid1.image.") &&
        (startswith(trimspace(var.operator_ssh_public_key), "ssh-ed25519 ") ||
        startswith(trimspace(var.operator_ssh_public_key), "ssh-rsa "))
      )
      error_message = "BLOCKED: reviewed Bastion/private network/cost/quota/management and pinned official image/AD/personal SSH PUBLIC key required."
    }
  }
  depends_on = [oci_core_network_security_group_security_rule.bastion_ssh]
}

# The target-restricted policy is a proposal, not proof of effective IAM permissions.
resource "oci_identity_policy" "bastion_operator" {
  count          = var.proposal_bastion_iam ? 1 : 0
  compartment_id = try(one(data.oci_identity_compartments.hic[0].compartments).id, null)
  name           = "hic-bastion-operator"
  description    = "Proposed scoped Bastion port-forward operator access"
  statements = [
    "Allow group id ${var.bastion_operator_group_ocid} to use bastion in compartment id ${try(one(data.oci_identity_compartments.hic[0].compartments).id, "")} where target.bastion.ocid = '${try(oci_bastion_bastion.management[0].id, "")}'",
    "Allow group id ${var.bastion_operator_group_ocid} to manage bastion-session in compartment id ${try(one(data.oci_identity_compartments.hic[0].compartments).id, "")} where target.resource.ocid = '${try(oci_core_instance.sandbox_host[0].id, "")}'",
    "Allow group id ${var.bastion_operator_group_ocid} to read instances in compartment id ${try(one(data.oci_identity_compartments.hic[0].compartments).id, "")}",
    "Allow group id ${var.bastion_operator_group_ocid} to read vcn in compartment id ${try(one(data.oci_identity_compartments.hic[0].compartments).id, "")}",
    "Allow group id ${var.bastion_operator_group_ocid} to read subnets in compartment id ${try(one(data.oci_identity_compartments.hic[0].compartments).id, "")}"
  ]
  lifecycle {
    precondition {
      condition     = var.proposal_network && var.proposal_bastion && var.proposal_vm && var.owner_private_management_reviewed && local.bastion_group_valid
      error_message = "BLOCKED: reviewed network, Bastion, VM and existing operator group identity are required."
    }
  }
}
