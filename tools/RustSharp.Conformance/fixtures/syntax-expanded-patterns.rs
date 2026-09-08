fn f() {
    let ref mut x = y;
    let &[head, ref tail @ ..] = values;
    let Pair { first: n @ 1..=9, ref mut second, .. } = value;
    let (a, .., b) = tuple;
    let Some::<T>(x) = value;
    match value { <T as Trait>::VALUE => 0, Some(x) | Other(x) => x, _ => 1 };
}
