# Moving the existing data (database, token keys, uploaded files) to the server

Skip this for a clean start. Do it to keep the published-post history, the leaderboard data and the connected Facebook Page.

On this PC (stack running), from the `infra` folder:

    docker compose -f docker-compose.prod.yml --env-file prod.env -p growth exec -T postgres pg_dump -U growth -d growthengine -Fc > growth.dump
    docker run --rm -v growth_keys:/v -v "%CD%":/out alpine tar czf /out/keys.tgz -C /v .
    docker run --rm -v growth_objects:/v -v "%CD%":/out alpine tar czf /out/objects.tgz -C /v .

Copy the three files to the server (S3 or SSM), then there, after `up -d postgres`:

    docker compose ... exec -T postgres pg_restore -U growth -d growthengine --clean --if-exists < growth.dump
    docker run --rm -v growth_keys:/v -v $PWD:/in alpine tar xzf /in/keys.tgz -C /v
    docker run --rm -v growth_objects:/v -v $PWD:/in alpine tar xzf /in/objects.tgz -C /v
    docker compose ... up -d

The keys volume must come with the database, or stored Facebook tokens cannot be decrypted (reconnect the Page if it is lost).
