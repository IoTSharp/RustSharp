use SourceProducer::first;
fn main() {
    let owner = 42;
    let view = first((&owner, 1));
    println!("{}", *view);
}
