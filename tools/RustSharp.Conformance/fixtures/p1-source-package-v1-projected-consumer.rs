use SourceProducer::first;
fn main() {
    let owner = (42, 1);
    let view = first(&owner);
    println!("{}", *view);
}
