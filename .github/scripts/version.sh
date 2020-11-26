#!/bin/bash
set -eu

output_path=$1
git_ref=$GITHUB_REF
git_run=$GITHUB_RUN_NUMBER
git_sha=$GITHUB_SHA
version_prefix=$VERSION_PREFIX

if [ "$git_ref" = "refs/tags/v$version_prefix" ]; then
	version_suffix=""
elif [ "$git_ref" = "refs/heads/main" ]; then
	version_suffix="-rc.$git_run"
else
	version_suffix="-alpha.$git_run"
fi

if [ "${git_ref:0:11}" = "refs/heads/" ]; then
	repository_branch=${git_ref:11}
else
	repository_branch=""
fi

version="$version_prefix$version_suffix"
informational_version="$version+$git_sha"

cat <<EOT > $output_path
<Project>
	<PropertyGroup>
		<Version>$version</Version>
		<AssemblyVersion>$version_prefix</AssemblyVersion>
		<FileVersion>$version_prefix</FileVersion>
		<InformationalVersion>$informational_version</InformationalVersion>
		<RepositoryCommit>$git_sha</RepositoryCommit>
		<RepositoryBranch>$repository_branch</RepositoryBranch>
	</PropertyGroup>
</Project>
EOT

cat $output_path
