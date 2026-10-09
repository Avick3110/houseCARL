- **A `housecarl_records` scan scoped to several plugins now shows each record's highest-loading scoped copy, not
  whichever plugin was named first, and `where=` judges that copy.** Rows come back in load order whatever the
  order of `plugins.names`, which changes `limit=`/`offset=` pages.
