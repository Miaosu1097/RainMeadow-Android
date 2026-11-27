#!/bin/sh
set -eu
WORKSPACE_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
. "$WORKSPACE_ROOT/tools/rain-meadow/env.sh"
cd "$WORKSPACE_ROOT"

case "$#:${1:-}" in
    2:check|3:upload) ;;
    *) printf '%s\n' 'Usage: draft-release.sh check TAG | draft-release.sh upload TAG GAME_REVISION' >&2; exit 2 ;;
esac
repository=${GITHUB_REPOSITORY:?Set the destination GitHub repository}
tag=$2
git check-ref-format "refs/tags/$tag"
commit=$(git rev-parse HEAD)
tag_commit=$(gh api --method GET "repos/$repository/commits" -f "sha=refs/tags/$tag" -f per_page=1 --jq '.[0].sha')
if [ "$tag_commit" != "$commit" ]; then
    printf '%s\n' "Release tag $tag points to $tag_commit. Select the workflow ref for $tag." >&2
    exit 1
fi
if [ "$1" = check ]; then
    exit 0
fi

revision=$3
zip=${RAIN_MEADOW_RELEASE_ZIP:-release/Rain-Meadow/$revision/RainMeadow-android.zip}
file=$(basename -- "$zip")
(cd "$(dirname -- "$zip")" && sha256sum "$file" > "$file.sha256")
notes=$(printf 'Rain Meadow Android ARM64 build for game snapshot %s.\n\nSource commit `%s`.\n' "$revision" "$commit")
gh release create --repo "$repository" --draft --verify-tag --title "$tag" --notes "$notes" \
    -- "$tag" "$zip" "$zip.sha256"
