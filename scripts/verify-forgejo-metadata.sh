#!/bin/sh
# Run inside the Forgejo container. Reads Git and SQLite without changing either.
set -eu
export LC_ALL=C

if [ "$#" -ne 3 ]; then
    echo "Usage: $0 DATABASE BARE_REPOSITORY REPOSITORY_ID" >&2
    exit 2
fi
database=$1
repository=$2
repo_id=$3
case "$repo_id" in ''|*[!0-9]*|0) echo 'Repository ID must be a positive integer' >&2; exit 2 ;; esac

query() {
    sqlite3 -readonly -batch -noheader -cmd '.timeout 5000' "$database" "$1"
}

if [ "$(query "SELECT count(*) FROM repository WHERE id=$repo_id;")" != 1 ]; then
    echo "Repository $repo_id is missing from the database" >&2
    exit 1
fi

failed=0
for hook in pre-receive update post-receive proc-receive; do
    for path in "$repository/hooks/$hook" "$repository/hooks/$hook.d/gitea"; do
        if [ ! -f "$path" ] || [ ! -x "$path" ]; then
            echo "Missing or non-executable Git hook: $path" >&2
            failed=1
        fi
    done
done

git_branches=$(git --git-dir="$repository" for-each-ref --sort=refname --format='%(objectname) %(refname)' refs/heads)
db_branches=$(query "SELECT commit_id || ' refs/heads/' || name FROM branch WHERE repo_id=$repo_id AND is_deleted=0 ORDER BY name COLLATE BINARY;")
if [ "$git_branches" != "$db_branches" ]; then
    echo 'Forgejo branch records differ from Git (names or commit IDs)' >&2
    failed=1
fi

tag_refs=$(git --git-dir="$repository" for-each-ref --sort=refname --format='%(refname)' refs/tags)
git_tags=$(
    for ref in $tag_refs; do
        commit=$(git --git-dir="$repository" rev-parse "$ref^{}")
        printf '%s %s\n' "$commit" "$ref"
    done
)
db_tags=$(query "SELECT sha1 || ' refs/tags/' || tag_name FROM release WHERE repo_id=$repo_id AND is_draft=0 ORDER BY tag_name COLLATE BINARY;")
if [ "$git_tags" != "$db_tags" ]; then
    echo 'Forgejo tag records differ from Git (names or target IDs)' >&2
    failed=1
fi

if [ "$failed" -ne 0 ]; then exit 1; fi
echo 'Forgejo hooks, branch records and tag records match Git.'
